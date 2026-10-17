namespace Ragamuffin.Data;

public static class PageSource
{
    public const string Embedded = "embedded";
    public const string Ocr = "ocr";

    /// <summary>The PDF's own words, arranged by layout analysis (reading order, no headers/footers, rebuilt tables).</summary>
    public const string Layout = "layout";
}

/// <summary>
/// One PDF page. Keeps the text embedded in the PDF and any local OCR text side by side, so OCR can be
/// reverted and re-run; <see cref="Source"/> says which one the index uses.
/// </summary>
public sealed class DocumentPage
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>1-based page number.</summary>
    public int Page { get; set; }

    public string? EmbeddedText { get; set; }
    public string? OcrText { get; set; }
    public string Source { get; set; } = PageSource.Embedded;
    public string? OcrModel { get; set; }
    public DateTimeOffset? OcrAt { get; set; }

    /// <summary>Why the embedded text looks like poor OCR, or null if it looks fine.</summary>
    public string? SuspectReason { get; set; }

    /// <summary>Embedded words arranged by layout analysis (Markdown), or null if the page hasn't been analyzed.</summary>
    public string? LayoutText { get; set; }

    /// <summary>Regions returned by the layout service (JSON), kept for review and re-assembly.</summary>
    public string? LayoutJson { get; set; }

    /// <summary>Rotation the layout service applied to make the page upright (0, 90, 180, 270).</summary>
    public int? LayoutAngle { get; set; }
    public DateTimeOffset? LayoutAt { get; set; }

    public string? EffectiveText => Source switch
    {
        PageSource.Ocr => OcrText,
        PageSource.Layout => LayoutText,
        _ => EmbeddedText,
    };

    /// <summary>What "embedded text" means for this page: the layout-arranged version when there is one.</summary>
    public string EmbeddedSource => LayoutText is not null ? PageSource.Layout : PageSource.Embedded;
}
