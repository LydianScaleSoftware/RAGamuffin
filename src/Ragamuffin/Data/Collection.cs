namespace Ragamuffin.Data;

/// <summary>
/// A named group of documents. Documents reference collections by name (with ON UPDATE CASCADE),
/// so renaming a collection is a single-row update.
/// </summary>
public sealed class Collection
{
    /// <summary>Built-in collection for documents not filed anywhere else; cannot be renamed or removed.</summary>
    public const string Default = "default";

    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>What the collection holds, in a sentence or two; shown to agents in the MCP server instructions.</summary>
    public string? Description { get; set; }

    /// <summary>Topics and terms that help an agent decide to search this collection.</summary>
    public List<string> Keywords { get; set; } = [];
}
