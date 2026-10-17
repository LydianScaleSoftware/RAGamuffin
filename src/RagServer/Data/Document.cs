namespace RagServer.Data;

public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";
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
    public string? Error { get; set; }

    /// <summary>Full extracted text, filled in once ingestion succeeds.</summary>
    public string? Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
}
