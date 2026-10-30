namespace Ragamuffin;

public sealed class RagOptions
{
    public const string Section = "Rag";

    public string OllamaUrl { get; set; } = "http://10.10.10.2:11434";
    public string EmbeddingModel { get; set; } = "qwen3-embedding:0.6b";
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// Longest text sent to the embedding model per chunk. qwen3-embedding reads 32,768 tokens, so this covers the
    /// largest chunk Claude may define; the header, title and summary come first either way. With a model that reads
    /// less (nomic-embed-text stops at 2,048 tokens), anything past its limit is silently ignored.
    /// </summary>
    public int EmbeddingMaxChars { get; set; } = 60000;

    /// <summary>
    /// Text put before documents and queries when embedding. qwen3-embedding wants a task instruction on queries only;
    /// nomic-embed-text used "search_document: " and "search_query: ".
    /// </summary>
    public string DocumentPrefix { get; set; } = "";
    public string QueryPrefix { get; set; } = "Instruct: Given a question about the user's documents, retrieve the passages that answer it\nQuery: ";

    public int ChunkTargetChars { get; set; } = 1200;
    public int ChunkMaxChars { get; set; } = 1800;
    public int ChunkOverlapChars { get; set; } = 150;

    /// <summary>Where uploaded originals are kept. Relative paths resolve against the app's content root.</summary>
    public string FilesDirectory { get; set; } = "data/files";

    /// <summary>Largest file accepted per upload. A request may carry up to ten times this (the UI sends files in tens).</summary>
    public long MaxUploadBytes { get; set; } = 500L * 1024 * 1024;

    public OcrOptions Ocr { get; set; } = new();
    public LayoutOptions Layout { get; set; } = new();
    public CollectionOptions Collections { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public ClaudeOptions Claude { get; set; } = new();
}

/// <summary>Correction jobs the web UI hands to Claude Code, which runs headless (claude -p) with the library's tools.</summary>
public sealed class ClaudeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>The claude command. Empty looks in ~/.local/bin, then the PATH.</summary>
    public string Command { get; set; } = "";

    /// <summary>Model alias or id passed to --model, e.g. "sonnet" or "opus". Empty uses Claude Code's default.</summary>
    public string Model { get; set; } = "";

    /// <summary>The MCP endpoint Claude Code connects to; this service's own /mcp.</summary>
    public string McpUrl { get; set; } = "http://localhost:5077/mcp";

    /// <summary>
    /// Folder Claude Code runs in, and where finished jobs are kept. Relative paths resolve against the folder that
    /// holds FilesDirectory (so data/claude by default).
    /// </summary>
    public string WorkingDirectory { get; set; } = "claude";

    /// <summary>Stop a job that runs longer than this.</summary>
    public int TimeoutMinutes { get; set; } = 360;

    /// <summary>Passed to --max-budget-usd when set. Only applies when Claude Code bills an API key.</summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>Finished jobs kept for the web UI.</summary>
    public int KeepJobs { get; set; } = 100;
}

public sealed class CollectionOptions
{
    /// <summary>Ollama model that drafts collection descriptions and keywords ("Suggest" on the Collections page).</summary>
    public string SuggestModel { get; set; } = "qwen3:8b";

    public int SuggestTimeoutSeconds { get; set; } = 180;
}

/// <summary>What the MCP server tells agents about the library.</summary>
public sealed class McpOptions
{
    /// <summary>
    /// Token budget for each block of text an agent receives about the library: the server instructions added to its
    /// system prompt, and the list_collections tool's answer. Estimated at about 3.5 characters per token.
    /// </summary>
    public int ContextTokens { get; set; } = 2000;

    /// <summary>
    /// Folders import_file and add_document may read files from (they run on this machine, so Claude can hand over a
    /// path instead of the whole text). Empty means the home folder of the account the service runs as.
    /// </summary>
    public List<string> ImportRoots { get; set; } = [];

    /// <summary>
    /// Largest binary file import_file and add_document accept. Binaries are stored as a hex dump (about 4.9 times the
    /// file's size as text) alongside the original bytes; the web upload still rejects them.
    /// </summary>
    public long MaxBinaryBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Largest chunk Claude may define with add_document or set_chunks. Big enough for a long section or a whole
    /// function; past it the chunk has to be split.
    /// </summary>
    public int MaxChunkChars { get; set; } = 60000;

    /// <summary>Results search_documents returns when the agent doesn't ask for a number (max 25).</summary>
    public int DefaultResults { get; set; } = 8;

    /// <summary>Token budget for one search_documents answer; passages are excerpted around the query terms to fit.</summary>
    public int SearchTokens { get; set; } = 12000;

    /// <summary>Token budget for one get_document answer (an outline, or the requested pages, lines or section).</summary>
    public int DocumentTokens { get; set; } = 25000;

    /// <summary>Token budget for one list_documents answer.</summary>
    public int ListTokens { get; set; } = 6000;

    /// <summary>
    /// Ollama model that writes ask_library's answers from retrieved passages. Empty (the default) leaves ask_library
    /// out: Claude reads the passages itself. Set it (e.g. "qwen3:8b") for agents that benefit from a pre-written answer.
    /// </summary>
    public string AnswerModel { get; set; } = "";

    /// <summary>Token budget for the passages given to the answer model.</summary>
    public int AnswerContextTokens { get; set; } = 3000;

    /// <summary>Longest answer the answer model may write, in tokens.</summary>
    public int AnswerMaxTokens { get; set; } = 700;

    public int AnswerTimeoutSeconds { get; set; } = 180;
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
