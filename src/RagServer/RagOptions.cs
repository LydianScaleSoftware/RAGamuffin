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

    /// <summary>Largest file accepted per upload. A request may carry up to ten times this (the UI sends files in tens).</summary>
    public long MaxUploadBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>get_document truncates full text above this many characters.</summary>
    public int MaxDocumentChars { get; set; } = 60_000;

    public OcrOptions Ocr { get; set; } = new();
    public LayoutOptions Layout { get; set; } = new();
}

/// <summary>Page layout analysis (orientation, regions, reading order, tables) by the layout service.</summary>
public sealed class LayoutOptions
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://localhost:5002";

    /// <summary>Resolution pages are rendered at for layout analysis and region OCR. Higher reads small print better.</summary>
    public int Dpi { get; set; } = 200;

    public int PageTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Pages are downscaled to this longest side for layout analysis only (region OCR still crops the full-resolution
    /// render). Keeps the layout service within memory on large pages.
    /// </summary>
    public int MaxImageEdge { get; set; } = 2000;

    /// <summary>OCR the text inside figures (diagram labels) and keep it as figure text.</summary>
    public bool IncludeFigureText { get; set; } = true;

    /// <summary>Queue layout analysis automatically for PDFs with text when they're uploaded.</summary>
    public bool AutoOnImport { get; set; }
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

    /// <summary>Cap on tokens generated per image; output cut off by the cap is treated as a failed read (usually a repetition loop).</summary>
    public int MaxOutputTokens { get; set; } = 4096;
    public int PageTimeoutSeconds { get; set; } = 300;

    /// <summary>How long Ollama keeps the OCR model loaded after the last page.</summary>
    public string KeepAlive { get; set; } = "5m";

    /// <summary>Sent as Ollama's "think" flag when set; use false for reasoning models such as qwen3.8.</summary>
    public bool? Think { get; set; }

    /// <summary>Refuse analysis jobs (OCR plus layout) covering more than this many pages of one document.</summary>
    public int MaxPages { get; set; } = 2000;

    /// <summary>OCR PDFs without text as soon as they're uploaded instead of waiting for "Run OCR".</summary>
    public bool AutoOnImport { get; set; }
}
