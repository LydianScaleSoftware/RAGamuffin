namespace Ragamuffin.Data;

public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";

    /// <summary>PDF with no usable text; waits for the user to run OCR.</summary>
    public const string NeedsOcr = "needs_ocr";

    /// <summary>OCR queued or running; see <see cref="Document.ProgressDone"/>.</summary>
    public const string Ocr = "ocr";
}

public static class Encodings
{
    /// <summary>A hex dump: offset, 16 bytes in hex, and their printable ASCII, per line (like xxd).</summary>
    public const string Hex = "hex";
}

/// <summary>
/// Who chose a document's chunk boundaries. Chunks Claude chooses are called Claudomatics (Claude + automatic) in the
/// UI and docs.
/// </summary>
public static class ChunkSources
{
    /// <summary>The server's chunker cut the text.</summary>
    public const string Auto = "auto";

    /// <summary>Claude chose the chunk boundaries (add_document / set_chunks): Claudomatics. Re-indexing keeps them.</summary>
    public const string Claude = "claude";
}

public sealed class Document
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Collection { get; set; }

    /// <summary>markdown | text | code | pdf</summary>
    public required string ContentType { get; set; }
    public string? Language { get; set; }

    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public string Status { get; set; } = DocumentStatus.Pending;
    /// <summary>Failure reason, or a notice such as a cancelled OCR run.</summary>
    public string? Error { get; set; }

    // PDF page statistics, refreshed whenever the document is (re)indexed.
    public int PageCount { get; set; }
    public int OcrPages { get; set; }
    public int EmptyPages { get; set; }
    public int SuspectPages { get; set; }

    /// <summary>Pages with an OCR or layout <see cref="DocumentPage.Issue"/> that Claude hasn't checked yet.</summary>
    public int IssuePages { get; set; }

    /// <summary>Pages whose text was arranged by layout analysis (either OCR'd by region or embedded words placed by region).</summary>
    public int LayoutPages { get; set; }
    public string? OcrModel { get; set; }
    public DateTimeOffset? OcrAt { get; set; }

    /// <summary>OCR progress while <see cref="Status"/> is <see cref="DocumentStatus.Ocr"/>; total is null while queued.</summary>
    public int? ProgressDone { get; set; }
    public int? ProgressTotal { get; set; }

    /// <summary>OCR scope of the queued or running analysis job ("missing", "suspect", "all"), or null for no OCR.</summary>
    public string? OcrScope { get; set; }

    /// <summary>Layout analysis of pages with embedded text in that job: null (none), "new" (not yet analyzed) or "redo".</summary>
    public string? LayoutMode { get; set; }

    /// <summary>When the queued or running analysis job was requested; pages finished since then count as done on resume.</summary>
    public DateTimeOffset? OcrRequestedAt { get; set; }

    /// <summary>Who chose the chunk boundaries: <see cref="ChunkSources.Auto"/> or <see cref="ChunkSources.Claude"/>.</summary>
    public string ChunkSource { get; set; } = ChunkSources.Auto;

    /// <summary>
    /// The document this one was made from, e.g. the PDF a Markdown conversion came from. While this document is
    /// ready, the source's own chunks are left out of search so results aren't duplicated.
    /// </summary>
    public Guid? SourceDocumentId { get; set; }

    /// <summary>
    /// How a binary file was turned into text for the index: <see cref="Encodings.Hex"/>, or null for documents that
    /// are text already. The original bytes stay in the file store.
    /// </summary>
    public string? Encoding { get; set; }

    /// <summary>Full extracted text, filled in once ingestion succeeds.</summary>
    public string? Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
    public List<DocumentPage> Pages { get; set; } = [];
}
