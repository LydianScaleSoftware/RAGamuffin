namespace RagServer.Data;

public static class PageSource
{
    public const string Embedded = "embedded";
    public const string Ocr = "ocr";
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

    public string? EffectiveText => Source == PageSource.Ocr ? OcrText : EmbeddedText;
}
