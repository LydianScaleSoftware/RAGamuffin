using NpgsqlTypes;
using Pgvector;

namespace Ragamuffin.Data;

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

    /// <summary>Last PDF page the chunk covers when it spans several (Claude-chosen chunks of a converted manual).</summary>
    public int? PageEnd { get; set; }

    /// <summary>The chunk's own title, given by Claude (e.g. "Listing 10-1: Writing text to the graphics screen").</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Claude's one- or two-sentence description. It's embedded along with the start of the text, so a chunk longer
    /// than the embedding model's context is still found by what it's about.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>What the chunk is: prose, table, example, code, function, … (Claude-chosen chunks only).</summary>
    public string? Kind { get; set; }

    /// <summary>Function, class or other code symbol the chunk defines, for code chunks.</summary>
    public string? Symbol { get; set; }

    /// <summary>True when the text came from local OCR rather than the file itself.</summary>
    public bool Ocr { get; set; }

    public Vector? Embedding { get; set; }

    /// <summary>Generated column (title, symbol, heading path, summary, content) for full-text search.</summary>
    public NpgsqlTsVector Tsv { get; set; } = null!;
}
