using System.ComponentModel;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Ragamuffin.Data;
using Ragamuffin.Search;

namespace Ragamuffin.Mcp;

/// <summary>
/// The read-only MCP tools. Every answer fits a token budget (Rag:Mcp), sized for Claude, with a hint for reading more.
/// </summary>
[McpServerToolType]
public sealed class RagTools(RagDbContext db, SearchService search, LibraryGuide guide, DocumentReader reader,
    IOptions<RagOptions> options)
{
    McpOptions Mcp => options.Value.Mcp;

    [McpServerTool(Name = "search_documents", ReadOnly = true)]
    [Description("Search the user's document library (manuals, references, code) by meaning and by exact words. Returns cited passages with their chunk ids; get_chunk returns a chunk whole, get_document reads around it. If the first search misses, rephrase with the terms the manual itself would use.")]
    public async Task<string> SearchDocuments(
        [Description("Question or keywords; exact identifiers (DrawCString, $A604) work.")] string query,
        [Description("Number of results (default 8, max 25).")] int? top_k = null,
        [Description("Search only this collection (muted or not).")] string? collection = null,
        [Description("Search only these collections, e.g. [\"System 6 Docs\"]. Naming a muted collection searches it.")] List<string>? collections = null,
        [Description("Leave these collections out of this search.")] List<string>? exclude = null,
        CancellationToken ct = default)
    {
        var wanted = Math.Clamp(top_k ?? Mcp.DefaultResults, 1, 25);
        var named = (collections ?? []).Concat(string.IsNullOrWhiteSpace(collection) ? [] : [collection]).ToList();
        var scope = await search.ResolveAsync(new SearchScope(named.Count > 0 ? named : null, exclude), ct);
        if (scope.Unknown.Count > 0)
        {
            var names = await db.Collections.AsNoTracking().OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);
            return $"No collection named {string.Join(", ", scope.Unknown.Select(n => $"\"{n}\""))}. Collections: {string.Join(", ", names)}.";
        }
        if (scope.Searched.Count == 0)
            return "Nothing to search: every collection is excluded" + (scope.MutedSkipped.Count > 0 ? $" or muted ({string.Join(", ", scope.MutedSkipped)})" : "") + ".";
        // Fetch extra candidates: merging neighbours and dropping duplicates can shrink the list.
        var hits = await search.SearchAsync(query, Math.Min(50, wanted * 4), new SearchScope(scope.Searched), ct);
        var passages = LeanText.Dedupe(LeanText.Merge(hits), query).Take(wanted).ToList();
        var result = LeanText.FormatSearch(query, passages, Mcp.SearchTokens);
        return scope.MutedSkipped.Count == 0 ? result :
            $"Muted collections were not searched: {string.Join(", ", scope.MutedSkipped)}. Name one in `collections` to search it " +
            "(e.g. if the answer isn't here, or the user asked for that version).\n\n" + result;
    }

    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("Read a document: its outline by default, or pages=, lines= or section=. Long reads end with where to continue.")]
    public Task<string> GetDocument(
        [Description("Document name (partial names work) or id.")] string name_or_id,
        [Description("PDF pages, e.g. \"12\" or \"12-14\".")] string? pages = null,
        [Description("Text or code lines, e.g. \"120-200\".")] string? lines = null,
        [Description("A section heading to read, e.g. \"QuickDraw II\".")] string? section = null,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default) => reader.ReadAsync(name_or_id, collection, pages, lines, section, ct);

    [McpServerTool(Name = "get_chunk", ReadOnly = true)]
    [Description("Return chunks whole, by the ids search results show, with their title, summary, location and the chunks around them if asked.")]
    public async Task<string> GetChunk(
        [Description("Chunk ids from search results, e.g. [48213] or [48213, 48214].")] List<long> ids,
        [Description("Also return this many chunks before and after each one (default 0, max 3).")] int neighbors = 0,
        CancellationToken ct = default)
    {
        if (ids is not { Count: > 0 }) return "No chunk ids given.";
        neighbors = Math.Clamp(neighbors, 0, 3);
        var anchors = await db.Chunks.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.DocumentId, c.Ordinal }).ToListAsync(ct);
        if (anchors.Count == 0) return "No chunks with those ids (they change when a document is re-indexed; search again).";

        var wanted = anchors.SelectMany(a => Enumerable.Range(a.Ordinal - neighbors, 2 * neighbors + 1).Select(o => (a.DocumentId, o))).ToHashSet();
        var docIds = anchors.Select(a => a.DocumentId).Distinct().ToList();
        var minOrd = anchors.Min(a => a.Ordinal) - neighbors;
        var maxOrd = anchors.Max(a => a.Ordinal) + neighbors;
        var rows = (await db.Chunks.AsNoTracking()
                .Where(c => docIds.Contains(c.DocumentId) && c.Ordinal >= minOrd && c.Ordinal <= maxOrd)
                .Select(c => new
                {
                    c.Id, c.DocumentId, c.Ordinal, c.Content, c.HeadingPath, c.Title, c.Summary, c.Kind, c.Symbol,
                    c.StartLine, c.EndLine, c.Page, c.PageEnd, c.Ocr, c.Document.Name, c.Document.Collection, c.Document.Encoding,
                })
                .ToListAsync(ct))
            .Where(c => wanted.Contains((c.DocumentId, c.Ordinal)))
            .OrderBy(c => c.Name).ThenBy(c => c.Ordinal).ToList();

        var budget = LeanText.Chars(Mcp.DocumentTokens);
        var sb = new StringBuilder();
        var shown = 0;
        foreach (var c in rows)
        {
            var p = new Passage(c.Name, c.Collection, c.HeadingPath, c.StartLine, c.EndLine, c.Page, c.PageEnd, c.Ocr, c.Content, [])
            {
                ChunkIds = [c.Id], Title = c.Title, Summary = c.Summary, Kind = c.Kind, Symbol = c.Symbol, Encoding = c.Encoding,
            };
            var block = $"[{LeanText.Citation(p)}]{(ids.Contains(c.Id) ? "" : " (neighbour)")}\n" +
                        (c.Summary is null ? "" : $"Summary: {c.Summary}\n") + c.Content + "\n\n";
            if (shown > 0 && sb.Length + block.Length > budget)
            {
                sb.Append($"…{rows.Count - shown} more chunk(s) didn't fit; ask for fewer ids at a time.");
                break;
            }
            sb.Append(block);
            shown++;
        }
        var missing = ids.Except(anchors.Select(a => a.Id)).ToList();
        if (missing.Count > 0) sb.Append($"\nNo chunks with id {string.Join(", ", missing)} (re-indexed since; search again).");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_documents", ReadOnly = true)]
    [Description("Find documents by name; returns names, types, sizes, chunk counts, conversions and any notice (e.g. pages with OCR problems).")]
    public async Task<string> ListDocuments(
        [Description("Part of a document name, e.g. \"toolbox\".")] string? filter = null,
        [Description("Limit to one collection.")] string? collection = null,
        CancellationToken ct = default)
    {
        var q = db.Documents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(collection)) q = q.Where(d => d.Collection == collection.Trim());
        if (!string.IsNullOrWhiteSpace(filter)) q = q.Where(d => EF.Functions.ILike(d.Name, $"%{filter.Trim()}%"));
        var docs = await q.OrderBy(d => d.Collection).ThenBy(d => d.Name)
            .Select(d => new
            {
                d.Collection, d.Name, d.ContentType, d.Language, d.PageCount, d.ChunkCount, d.Status, d.OcrPages, d.ChunkSource, d.Encoding, d.SizeBytes,
                d.Error, d.IssuePages,
                Source = db.Documents.Where(o => o.Id == d.SourceDocumentId).Select(o => o.Name).FirstOrDefault(),
                ConvertedTo = db.Documents.Where(o => o.SourceDocumentId == d.Id).Select(o => o.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        if (docs.Count == 0)
            return "No documents match" + (filter is null ? "" : $" \"{filter}\"") + (collection is null ? "" : $" in {collection}") + ".";

        var muted = await db.Collections.AsNoTracking().Where(c => c.Muted).Select(c => c.Name).ToListAsync(ct);
        var budget = LeanText.Chars(Mcp.ListTokens);
        var sb = new StringBuilder($"{docs.Count} document{(docs.Count == 1 ? "" : "s")}:\n");
        string? current = null;
        var shown = 0;
        foreach (var d in docs)
        {
            var line = new StringBuilder();
            if (d.Collection != current) line.Append($"{d.Collection}{(muted.Contains(d.Collection) ? " (muted: searched only when named)" : "")}:\n");
            var kind = d.ContentType == "pdf" ? $"pdf, {d.PageCount} pages{(d.OcrPages > 0 ? ", OCR" : "")}"
                : d.Encoding is not null ? $"⟦ENCODED BINARY · {d.Encoding} dump⟧ {d.SizeBytes:N0} bytes"
                : d.Language ?? d.ContentType;
            line.Append($"- {d.Name} ({kind}, {d.ChunkCount} chunks{(d.ChunkSource == ChunkSources.Claude ? ", Claudomatics" : "")}" +
                        $"{(d.Status == DocumentStatus.Ready ? "" : ", " + d.Status.Replace('_', ' '))})" +
                        (d.Source is null ? "" : $" — converted from {d.Source}") +
                        (d.ConvertedTo is null ? "" : $" — searched through its conversion {d.ConvertedTo}") +
                        (d.Status == DocumentStatus.Ready && d.Error is not null ? $" — NOTICE: {d.Error}" : "") + "\n");
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
