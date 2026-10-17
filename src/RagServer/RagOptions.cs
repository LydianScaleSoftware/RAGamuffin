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

    public OcrOptions Ocr { get; set; } = new();
}

/// <summary>Local OCR of PDF pages with a vision model on the Ollama server.</summary>
public sealed class OcrOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Ollama vision model; replaceable. Each document records which model produced its OCR text.</summary>
    public string Model { get; set; } = "glm-ocr";

    /// <summary>Instruction sent with each page image. glm-ocr expects "Text Recognition:".</summary>
    public string Prompt { get; set; } = "Text Recognition:";

    public int Dpi { get; set; } = 150;

    /// <summary>Rendered pages are scaled down so their longer side is at most this many pixels.</summary>
    public int MaxImageEdge { get; set; } = 1800;

    public int NumCtx { get; set; } = 16384;
    public int PageTimeoutSeconds { get; set; } = 300;

    /// <summary>How long Ollama keeps the OCR model loaded after the last page.</summary>
    public string KeepAlive { get; set; } = "5m";

    /// <summary>Sent as Ollama's "think" flag when set; use false for reasoning models such as qwen3.8.</summary>
    public bool? Think { get; set; }

    /// <summary>Refuse OCR runs longer than this many pages.</summary>
    public int MaxPages { get; set; } = 500;

    /// <summary>OCR PDFs without text as soon as they're uploaded instead of waiting for "Run OCR".</summary>
    public bool AutoOnImport { get; set; }
}
