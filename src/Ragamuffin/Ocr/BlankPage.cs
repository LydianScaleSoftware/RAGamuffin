using SkiaSharp;

namespace Ragamuffin.Ocr;

/// <summary>
/// Tells a genuinely blank page (a scanned empty side, a separator) from a page with content but no text, from a
/// small rendering of the page.
/// </summary>
public static class BlankPage
{
    const int Dpi = 40;
    // Scans have dark edges, binding holes and spiral bindings near the borders, so only the middle is looked at.
    const double Margin = 0.1;
    // A pixel counts as ink when it's this much darker than the paper; a blank page has almost none.
    const int InkContrast = 60;
    const double MaxInkShare = 0.002;

    public static bool IsBlank(byte[] pdf, int pageIndex)
    {
        using var bitmap = OcrEngine.RenderBitmap(pdf, pageIndex, Dpi);
        return IsBlank(bitmap);
    }

    public static bool IsBlank(SKBitmap bitmap)
    {
        int x0 = (int)(bitmap.Width * Margin), x1 = bitmap.Width - x0;
        int y0 = (int)(bitmap.Height * Margin), y1 = bitmap.Height - y0;
        if (x1 <= x0 || y1 <= y0) return false;

        var luma = new List<int>((x1 - x0) * (y1 - y0));
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var c = bitmap.GetPixel(x, y);
                luma.Add((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            }
        if (luma.Count == 0) return false;
        // The paper's tone is the brightest common value (tinted or yellowed scans aren't white).
        var sorted = luma.Order().ToList();
        var paper = sorted[(int)(sorted.Count * 0.9)];
        var ink = luma.Count(l => l < paper - InkContrast);
        return ink / (double)luma.Count <= MaxInkShare;
    }
}
