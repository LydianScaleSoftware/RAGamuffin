namespace RagServer.Data;

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
}
