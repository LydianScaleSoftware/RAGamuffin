namespace RagServer;

public sealed class RagOptions
{
    public const string Section = "Rag";

    public string OllamaUrl { get; set; } = "http://10.10.10.2:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>Prefixes required by nomic-embed-text; set both to "" for models that don't use them.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";

    public int ChunkTargetChars { get; set; } = 1200;
    public int ChunkMaxChars { get; set; } = 1800;
    public int ChunkOverlapChars { get; set; } = 150;

    /// <summary>Where uploaded originals are kept. Relative paths resolve against the app's content root.</summary>
    public string FilesDirectory { get; set; } = "data/files";

    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>get_document truncates full text above this many characters.</summary>
    public int MaxDocumentChars { get; set; } = 60_000;
}
