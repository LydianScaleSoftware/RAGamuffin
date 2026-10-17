using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using RagServer.Data;

namespace RagServer.Ingestion;

/// <summary>Embedded text of one PDF page plus the reason it looks like poor OCR, if any.</summary>
public sealed record EmbeddedPage(int Page, string Text, string? SuspectReason);

/// <summary>Which pages an OCR run covers.</summary>
public enum OcrScope
{
    /// <summary>Pages with no usable text.</summary>
    MissingText,
    /// <summary>Pages with no text, plus pages whose embedded text looks like poor OCR.</summary>
    Suspect,
    /// <summary>Every page, replacing embedded text.</summary>
    All,
}

public static class OcrScopes
{
    public static OcrScope? Parse(string? name) => name switch
    {
        "missing" => OcrScope.MissingText,
        "suspect" => OcrScope.Suspect,
        "all" => OcrScope.All,
        _ => null,
    };
}

public static partial class PdfPages
{
    /// <summary>Pages with less text than this are treated as having none (scanner stamps, page numbers).</summary>
    public const int MinUsefulChars = 20;

    static readonly string[] OcrProducers =
        ["tesseract", "abbyy", "finereader", "paper capture", "ocrmypdf", "omnipage", "readiris", "clearscan", "ocr"];

    /// <summary>Reads each page's embedded text and checks whether it looks like an old OCR layer.</summary>
    public static List<EmbeddedPage> Extract(byte[] bytes)
    {
        var pages = new List<EmbeddedPage>();
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var producer = OcrProducerName($"{pdf.Information.Producer} {pdf.Information.Creator}");
            foreach (var page in pdf.GetPages())
            {
                var text = Normalize(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
                var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                var invisible = letters.Count > 0
                    ? letters.Count(l => l.RenderingMode == TextRenderingMode.Neither) / (double)letters.Count
                    : 0;
                var pageArea = Math.Max(1, page.Width * page.Height);
                var imageCover = Math.Min(1, page.GetImages().Sum(i => Area(i.BoundingBox)) / pageArea);
                pages.Add(new EmbeddedPage(page.Number, text,
                    HasUsefulText(text) ? SuspectReason(invisible, imageCover, producer, text) : null));
            }
        }
        catch (Exception ex)
        {
            throw new ExtractionException($"Could not read PDF: {ex.Message}");
        }
        return pages;
    }

    public static bool HasUsefulText(string? text) =>
        text is not null && text.Count(c => !char.IsWhiteSpace(c)) >= MinUsefulChars;

    /// <summary>
    /// Explains why a page's embedded text is probably an old OCR layer, or returns null. Only a hint for the
    /// re-index dialog; nothing is re-OCR'd automatically.
    /// </summary>
    public static string? SuspectReason(double invisibleShare, double imageCover, string? ocrProducer, string text)
    {
        var scanned = imageCover >= 0.6;
        if (scanned && invisibleShare >= 0.5)
            return "Invisible text over a scanned image (typical of OCR'd scans)";
        if (scanned && ocrProducer is not null)
            return $"Scanned page with text from OCR software ({ocrProducer})";
        if (LooksGarbled(text))
            return "Text looks garbled";
        return null;
    }

    public static string? OcrProducerName(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        var m = metadata.ToLowerInvariant();
        return OcrProducers.FirstOrDefault(p => p == "ocr" ? OcrWord().IsMatch(m) : m.Contains(p));
    }

    [GeneratedRegex(@"\bocr\b")]
    private static partial Regex OcrWord();

    /// <summary>
    /// Conservative check for classic OCR noise: many words with stray symbols inside them, or text broken into
    /// single characters ("t h e  q u i c k").
    /// </summary>
    public static bool LooksGarbled(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 30) return false;

        var singles = tokens.Count(t => t.Length == 1 && char.IsLetter(t[0]));
        var odd = tokens.Count(IsOddToken);
        var replacement = text.Count(c => c == '�' || (char.IsControl(c) && c != '\n' && c != '\t'));

        return singles / (double)tokens.Length > 0.35
            || odd / (double)tokens.Length > 0.15
            || replacement > 5;
    }

    static bool IsOddToken(string token)
    {
        var core = token.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '«', '»', '“', '”', '‘', '’', '*');
        if (core.Length < 3) return false;
        if (!core.Any(char.IsLetter)) return false;
        // Symbols wedged between letters, e.g. "th~e", "w0r|d", "inf;ormation".
        for (var i = 1; i < core.Length - 1; i++)
        {
            var c = core[i];
            if (char.IsLetterOrDigit(c) || c is '-' or '\'' or '’' or '.' or '/' or '_' or '&' or '@' or ':') continue;
            if (char.IsLetter(core[i - 1]) && char.IsLetter(core[i + 1])) return true;
        }
        return false;
    }

    /// <summary>Picks the pages an OCR run should process.</summary>
    public static List<int> SelectForOcr(IEnumerable<DocumentPage> pages, OcrScope scope) => pages
        .Where(p => scope switch
        {
            OcrScope.All => true,
            OcrScope.Suspect => !HasUsefulText(p.EffectiveText) || (p.SuspectReason != null && p.Source == PageSource.Embedded),
            _ => !HasUsefulText(p.EffectiveText),
        })
        .Select(p => p.Page)
        .OrderBy(p => p)
        .ToList();

    /// <summary>Builds the document the chunker sees from the pages' current text sources.</summary>
    public static ExtractedDocument ToExtracted(IEnumerable<DocumentPage> pages)
    {
        var usable = pages.OrderBy(p => p.Page)
            .Where(p => HasUsefulText(p.EffectiveText))
            .Select(p => new PageText(p.Page, p.EffectiveText!))
            .ToList();
        var full = string.Join("\n\n", usable.Select(p => $"[Page {p.Page}]\n{p.Text}"));
        return new ExtractedDocument("pdf", null, full, usable);
    }

    static double Area(PdfRectangle r) => Math.Abs(r.Width * r.Height);

    static string Normalize(string text) =>
        string.Join('\n', TextSanitizer.Clean(text.Replace("\r\n", "\n").Replace('\r', '\n')).Split('\n').Select(l => l.TrimEnd())).Trim();
}
