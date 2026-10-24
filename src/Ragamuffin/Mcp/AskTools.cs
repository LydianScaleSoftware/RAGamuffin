using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Ragamuffin.Mcp;

/// <summary>
/// ask_library: a local Ollama model answers from retrieved passages. Only registered when Rag:Mcp:AnswerModel is
/// set; Claude reads the passages itself, so it's off by default.
/// </summary>
[McpServerToolType]
public sealed class AskTools(LibraryAnswerer answerer)
{
    [McpServerTool(Name = "ask_library", ReadOnly = true)]
    [Description("Answer a question from the user's document library in one step: a short answer by a local model, with code quoted exactly and cited sources.")]
    public Task<string> AskLibrary(
        [Description("The question, e.g. \"How does an ORCA/C program start QuickDraw II?\"")] string question,
        [Description("Limit to one collection.")] string? collection = null,
        CancellationToken ct = default) => answerer.AskAsync(question, collection, ct);
}
