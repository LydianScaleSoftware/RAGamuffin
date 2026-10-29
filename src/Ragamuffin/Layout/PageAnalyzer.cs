using System.Text.Json;
using Microsoft.Extensions.Options;
using Ragamuffin.Ocr;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Ragamuffin.Layout;

/// <summary>
/// Markdown for one page, the regions it was built from (JSON, in full-resolution pixels), the rotation that was
/// applied, and how many regions fell back to the layout service's own reading because glm-ocr failed on them.
/// </summary>
public sealed record PageLayoutResult(string Text, string RegionsJson, int Angle, int FallbackRegions = 0);

/// <summary>Runs one PDF page through layout analysis and assembles its text region by region.</summary>
public sealed class PageAnalyzer(LayoutClient layout, OcrEngine ocr, IOptions<RagOptions> options, ILogger<PageAnalyzer> log)
{
    LayoutOptions Opt => options.Value.Layout;

    /// <summary>
    /// Scanned page: orientation and layout from the layout service, then glm-ocr reads each region of the upright,
    /// full-resolution page with a prompt suited to it. Regions glm-ocr returns nothing for, or fails on, fall back to
    /// the layout service's own reading so no text is lost.
    /// </summary>
    public async Task<PageLayoutResult> OcrPageAsync(byte[] pdf, int pageIndex, CancellationToken ct)
    {
        using var rendered = OcrEngine.RenderBitmap(pdf, pageIndex, Opt.Dpi);
        var (result, regions) = await AnalyzeAsync(rendered, ct);
        using var upright = OcrEngine.Rotate(rendered, UprightRotation(result.Angle));

        var parts = new List<string?>();
        var fallbacks = 0;
        foreach (var region in regions)
        {
            if (LayoutAssembler.IsFurniture(region.Label, region.Text)) continue;
            if (LayoutAssembler.IsFigure(region.Label) && !Opt.IncludeFigureText) continue;

            string text;
            try
            {
                text = await ocr.TranscribeAsync(Crop(upright, region.Bbox), LayoutAssembler.OcrPrompt(region.Label), ct);
            }
            catch (OcrException ex)
            {
                log.LogWarning("glm-ocr failed on a {Label} region of page {Page}: {Error}; using the layout service's reading",
                    region.Label, pageIndex + 1, ex.Message);
                text = "";
                fallbacks++;
            }
            if (string.IsNullOrWhiteSpace(text)) text = region.Text;
            parts.Add(LayoutAssembler.FormatRegion(region.Label, text));
        }
        return new(LayoutAssembler.Join(parts), JsonSerializer.Serialize(regions), result.Angle, fallbacks);
    }

    /// <summary>
    /// Page with embedded text: layout regions filled with the PDF's own words, so nothing is re-OCR'd. Returns null
    /// for rotated pages, where mapping words to regions isn't reliable; the caller keeps the plain embedded text.
    /// </summary>
    public async Task<PageLayoutResult?> ArrangeEmbeddedAsync(byte[] pdf, int pageIndex, CancellationToken ct)
    {
        using var rendered = OcrEngine.RenderBitmap(pdf, pageIndex, Opt.Dpi);
        List<WordBox> words;
        bool upright;
        using (var doc = PdfDocument.Open(pdf))
        {
            var page = doc.GetPage(pageIndex + 1);
            if (page.Rotation.Value != 0) return null;
            words = ToImageSpace(page, rendered.Width, rendered.Height);
            upright = TextIsUpright(page);
        }

        // When the page's own text runs upright, the page is upright: don't let the classifier guess otherwise.
        var (result, regions) = await AnalyzeAsync(rendered, ct, detectOrientation: !upright);
        if (result.Angle != 0) return null;

        // Assign against every region, furniture included, so header and footer words are dropped rather than unplaced.
        var assigned = LayoutAssembler.AssignWords(regions.Select(r => r.Bbox).ToList(), words, out var unplaced);
        var parts = new List<string?>();
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var inRegion = assigned[i];
            if (inRegion.Count == 0 || LayoutAssembler.IsFurniture(region.Label, string.Join(' ', inRegion.Select(w => w.Text)))) continue;
            if (LayoutAssembler.IsFigure(region.Label) && !Opt.IncludeFigureText) continue;

            var text = region is { Label: "table", Table: { } table }
                ? LayoutAssembler.TableFromCells(table.Html, table.Cells, inRegion) ?? LayoutAssembler.ContentOrderLines(inRegion)
                : LayoutAssembler.WordsToLines(inRegion);
            parts.Add(region.Label == "table" ? text : LayoutAssembler.FormatRegion(region.Label, text));
        }
        // Words outside every region (the layout model missed them) are kept rather than lost.
        if (unplaced.Count > 0) parts.Add(LayoutAssembler.WordsToLines(unplaced));

        return new(LayoutAssembler.Join(parts), JsonSerializer.Serialize(regions), 0);
    }

    /// <summary>
    /// Sends a downscaled copy to the layout service (its models resize internally anyway, and full-size pages can
    /// exhaust its memory), then scales the regions back to full-resolution pixels.
    /// </summary>
    async Task<(LayoutResult Result, List<LayoutRegion> Regions)> AnalyzeAsync(SKBitmap rendered, CancellationToken ct, bool detectOrientation = true)
    {
        using var small = OcrEngine.Downscale(rendered, Opt.MaxImageEdge);
        var result = await layout.AnalyzeAsync(OcrEngine.EncodePng(small), ct, detectOrientation);
        var scale = rendered.Width / (double)small.Width;
        return (result, result.Regions.Select(r => LayoutAssembler.Scale(r, scale)).ToList());
    }

    /// <summary>Most of the page's letters run left to right, the right way up.</summary>
    public static bool TextIsUpright(UglyToad.PdfPig.Content.Page page)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        return letters.Count >= Ingestion.PdfPages.MinUsefulChars &&
               letters.Count(l => l.TextOrientation == UglyToad.PdfPig.Content.TextOrientation.Horizontal) >= letters.Count * 0.8;
    }

    /// <summary>The layout service reports the page's rotation; turning it this far clockwise makes it upright.</summary>
    public static int UprightRotation(int angle) => (360 - angle) % 360;

    /// <summary>The page's words in rendered-image pixels (origin top-left), using the crop box the renderer used.</summary>
    static List<WordBox> ToImageSpace(UglyToad.PdfPig.Content.Page page, int imageWidth, int imageHeight)
    {
        var crop = page.CropBox.Bounds;
        var sx = imageWidth / crop.Width;
        var sy = imageHeight / crop.Height;
        // The default word extractor keeps letters in content order; the nearest-neighbour one can scramble kerned
        // words (it produced "exendedt" for "extended").
        return page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => new WordBox(
                w.Text,
                (w.BoundingBox.Left - crop.Left) * sx,
                (crop.Top - w.BoundingBox.Top) * sy,
                (w.BoundingBox.Right - crop.Left) * sx,
                (crop.Top - w.BoundingBox.Bottom) * sy,
                w.Letters.Count > 0 ? w.Letters.Min(l => l.TextSequence) : 0))
            .ToList();
    }

    static byte[] Crop(SKBitmap image, double[] bbox, int pad = 8)
    {
        var rect = new SKRectI(
            Math.Max(0, (int)bbox[0] - pad), Math.Max(0, (int)bbox[1] - pad),
            Math.Min(image.Width, (int)Math.Ceiling(bbox[2]) + pad), Math.Min(image.Height, (int)Math.Ceiling(bbox[3]) + pad));
        using var subset = new SKBitmap();
        return image.ExtractSubset(subset, rect) ? OcrEngine.EncodePng(subset) : OcrEngine.EncodePng(image);
    }
}
