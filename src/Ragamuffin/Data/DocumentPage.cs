namespace Ragamuffin.Data;

public static class IssueKinds
{
    /// <summary>The page's text is probably wrong or missing.</summary>
    public const string Problem = "problem";

    /// <summary>Something was skipped or unusual, but the page keeps usable text.</summary>
    public const string Note = "note";
}

public static class PageSource
{
    public const string Embedded = "embedded";
    public const string Ocr = "ocr";

    /// <summary>The PDF's own words, arranged by layout analysis (reading order, no headers/footers, rebuilt tables).</summary>
    public const string Layout = "layout";

    /// <summary>Text Claude read from the page image and saved through the MCP set_page_text tool.</summary>
    public const string Claude = "claude";
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

    /// <summary>
    /// What went wrong the last time the page was OCR'd or laid out (no text found, glm-ocr failing on some regions,
    /// layout analysis failing), or null. Kept through re-indexing; the page is listed for review until it's OCR'd
    /// cleanly or Claude saves or confirms its text.
    /// </summary>
    public string? Issue { get; set; }

    /// <summary>
    /// How serious <see cref="Issue"/> is: <see cref="IssueKinds.Problem"/> (the text is probably wrong or missing) or
    /// <see cref="IssueKinds.Note"/> (something was skipped but the text is intact). Set together with the issue.
    /// </summary>
    public string? IssueKind { get; set; }

    /// <summary>
    /// The user muted this page's problem or note in the web UI: it no longer counts against the document's health.
    /// Cleared whenever a new run records a new issue.
    /// </summary>
    public bool IssueMuted { get; set; }

    /// <summary>True when a page without text is blank in its image too, false when it has content; null = not checked.</summary>
    public bool? Blank { get; set; }

    /// <summary>
    /// When the user marked the page's text as correct in the web UI. Cleared when the page's text changes, so the
    /// mark always refers to the text in use. (Claude's checks are recorded as source "claude" instead.)
    /// </summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>Text Claude verified or transcribed from the page image (Markdown), or null.</summary>
    public string? ClaudeText { get; set; }

    /// <summary>Claude's note on what it changed or found (e.g. "fixed OCR of table; figure caption added").</summary>
    public string? ClaudeNote { get; set; }
    public DateTimeOffset? ClaudeAt { get; set; }

    /// <summary>The source in use before Claude's text replaced it, so removing Claude's text restores it.</summary>
    public string? SourceBeforeClaude { get; set; }

    public string? EffectiveText => Source switch
    {
        PageSource.Ocr => OcrText,
        PageSource.Layout => LayoutText,
        PageSource.Claude => ClaudeText,
        _ => EmbeddedText,
    };

    /// <summary>What "embedded text" means for this page: the layout-arranged version when there is one.</summary>
    public string EmbeddedSource => LayoutText is not null ? PageSource.Layout : PageSource.Embedded;
}
