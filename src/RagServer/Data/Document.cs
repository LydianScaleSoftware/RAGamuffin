namespace RagServer.Data;

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
    public string? OcrModel { get; set; }
    public DateTimeOffset? OcrAt { get; set; }

    /// <summary>OCR progress while <see cref="Status"/> is <see cref="DocumentStatus.Ocr"/>; total is null while queued.</summary>
    public int? ProgressDone { get; set; }
    public int? ProgressTotal { get; set; }

    /// <summary>Scope of the queued or running OCR run ("missing", "suspect", "all"), so a restart can resume it.</summary>
    public string? OcrScope { get; set; }

    /// <summary>When the queued or running OCR run was requested; pages OCR'd since then count as done on resume.</summary>
    public DateTimeOffset? OcrRequestedAt { get; set; }

    /// <summary>Full extracted text, filled in once ingestion succeeds.</summary>
    public string? Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
    public List<DocumentPage> Pages { get; set; } = [];
}
