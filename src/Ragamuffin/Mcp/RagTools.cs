using System.ComponentModel;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Ragamuffin.Data;
using Ragamuffin.Search;

namespace Ragamuffin.Mcp;

/// <summary>
/// The MCP tools. Descriptions are deliberately short (they're sent with every request) and every answer fits a token
/// budget sized for small local models, with a hint for reading more.
/// </summary>
[McpServerToolType]
public sealed class RagTools(RagDbContext db, SearchService search, LibraryGuide guide, DocumentReader reader,
    LibraryAnswerer answerer, IOptions<RagOptions> options)
{
    McpOptions Mcp => options.Value.Mcp;

    [McpServerTool(Name = "ask_library", ReadOnly = true)]
    [Description("Answer a question from the user's document library in one step: a short answer with code quoted exactly and cited sources. Start here.")]
    public Task<string> AskLibrary(
        [Description("The question, e.g. \"How does an ORCA/C program start QuickDraw II?\"")] string question,
        [Description("Limit to one collection.")] string? collection = null,
        CancellationToken ct = default) => answerer.AskAsync(question, collection, ct);

    [McpServerTool(Name = "search_documents", ReadOnly = true)]
    [Description("Find passages in the user's document library. Returns short cited excerpts; read more around one with get_document.")]
    public async Task<string> SearchDocuments(
        [Description("Question or keywords; exact identifiers work.")] string query,
        [Description("Number of results (default 5, max 10).")] int? top_k = null,
        [Description("Limit to one collection.")] string? collection = null,
        CancellationToken ct = default)
    {
        var wanted = Math.Clamp(top_k ?? Mcp.DefaultResults, 1, 10);
        // Fetch extra candidates: merging neighbours and dropping duplicates can shrink the list.
        var hits = await search.SearchAsync(query, Math.Min(40, wanted * 4), collection, ct);
        var passages = LeanText.Dedupe(LeanText.Merge(hits), query).Take(wanted).ToList();
        return LeanText.FormatSearch(query, passages, Mcp.SearchTokens);
    }

    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("Read part of a document: its outline by default, or only pages=, lines= or section=.")]
    public Task<string> GetDocument(
        [Description("Document name (partial names work) or id.")] string name_or_id,
        [Description("PDF pages, e.g. \"12\" or \"12-14\".")] string? pages = null,
        [Description("Text or code lines, e.g. \"120-200\".")] string? lines = null,
        [Description("A section heading to read, e.g. \"QuickDraw II\".")] string? section = null,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default) => reader.ReadAsync(name_or_id, collection, pages, lines, section, ct);

    [McpServerTool(Name = "list_documents", ReadOnly = true)]
    [Description("Find documents by name; returns names, types and sizes.")]
    public async Task<string> ListDocuments(
        [Description("Part of a document name, e.g. \"toolbox\".")] string? filter = null,
        [Description("Limit to one collection.")] string? collection = null,
        CancellationToken ct = default)
    {
        var q = db.Documents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(collection)) q = q.Where(d => d.Collection == collection.Trim());
        if (!string.IsNullOrWhiteSpace(filter)) q = q.Where(d => EF.Functions.ILike(d.Name, $"%{filter.Trim()}%"));
        var docs = await q.OrderBy(d => d.Collection).ThenBy(d => d.Name)
            .Select(d => new { d.Collection, d.Name, d.ContentType, d.Language, d.PageCount, d.ChunkCount, d.Status, d.OcrPages })
            .ToListAsync(ct);
        if (docs.Count == 0)
            return "No documents match" + (filter is null ? "" : $" \"{filter}\"") + (collection is null ? "" : $" in {collection}") + ".";

        var budget = LeanText.Chars(Mcp.ListTokens);
        var sb = new StringBuilder($"{docs.Count} document{(docs.Count == 1 ? "" : "s")}:\n");
        string? current = null;
        var shown = 0;
        foreach (var d in docs)
        {
            var line = new StringBuilder();
            if (d.Collection != current) line.Append($"{d.Collection}:\n");
            var kind = d.ContentType == "pdf" ? $"pdf, {d.PageCount} pages{(d.OcrPages > 0 ? ", OCR" : "")}" : d.Language ?? d.ContentType;
            line.Append($"- {d.Name} ({kind}{(d.Status == DocumentStatus.Ready ? "" : ", " + d.Status.Replace('_', ' '))})\n");
            if (sb.Length + line.Length > budget - 90) break;
            sb.Append(line);
            current = d.Collection;
            shown++;
        }
        if (shown < docs.Count)
            sb.Append($"…and {docs.Count - shown} more. Narrow with filter=\"part of a name\" or collection=.");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_collections", ReadOnly = true)]
    [Description("What each collection covers, with keywords, sizes and example titles.")]
    public Task<string> ListCollections(
        [Description("Describe just this collection, in more detail.")] string? collection = null,
        CancellationToken ct = default) => guide.ListCollectionsAsync(collection, ct);
}
