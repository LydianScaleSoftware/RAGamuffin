using NpgsqlTypes;
using Pgvector;

namespace RagServer.Data;

public sealed class Chunk
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public int Ordinal { get; set; }
    public required string Content { get; set; }

    /// <summary>Markdown section breadcrumb, e.g. "Setup > Database".</summary>
    public string? HeadingPath { get; set; }

    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public int? Page { get; set; }

    /// <summary>True when the text came from local OCR rather than the file itself.</summary>
    public bool Ocr { get; set; }

    public Vector? Embedding { get; set; }

    /// <summary>Generated column (heading path + content) for full-text search.</summary>
    public NpgsqlTsVector Tsv { get; set; } = null!;
}
