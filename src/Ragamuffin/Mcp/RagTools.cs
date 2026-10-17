using System.ComponentModel;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Ragamuffin.Data;
using Ragamuffin.Search;

namespace Ragamuffin.Mcp;

[McpServerToolType]
public sealed class RagTools(RagDbContext db, SearchService search, LibraryGuide guide, IOptions<RagOptions> options)
{
    [McpServerTool(Name = "list_collections", ReadOnly = true)]
    [Description("Describe the collections in the user's document library: what each covers, keywords, size and example titles. Use it to choose a collection for search_documents.")]
    public Task<string> ListCollections(
        [Description("Only describe this collection, in more detail. Omit to describe all of them.")] string? collection = null,
        CancellationToken ct = default) => guide.ListCollectionsAsync(collection, ct);

    [McpServerTool(Name = "search_documents", ReadOnly = true)]
    [Description("Search the user's indexed documents (notes, docs, source code, PDFs) and return the most relevant passages with their source file and line/page. Use this whenever a question may be answered by the user's own documents. Phrase the query as a natural-language question or include exact identifiers.")]
    public async Task<string> SearchDocuments(
        [Description("What to look for.")] string query,
        [Description("Number of passages to return (1-20, default 8).")] int top_k = 8,
        [Description("Only search this collection (see list_documents). Omit to search everything.")] string? collection = null,
        CancellationToken ct = default)
    {
        var hits = await search.SearchAsync(query, Math.Clamp(top_k, 1, 20), collection, ct);
        if (hits.Count == 0)
            return $"No matching passages found for \"{query}\".";

        var sb = new StringBuilder();
        sb.AppendLine($"{hits.Count} passages for \"{query}\":");
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.AppendLine();
            sb.Append($"### [{i + 1}] {h.Collection}/{h.DocumentName}");
            if (h.StartLine is not null) sb.Append($" (lines {h.StartLine}-{h.EndLine})");
            if (h.Page is not null) sb.Append($" (page {h.Page}{(h.Ocr ? ", OCR" : "")})");
            sb.AppendLine();
            if (h.HeadingPath is not null) sb.AppendLine($"Section: {h.HeadingPath}");
            if (h.Ocr) sb.AppendLine($"Note: text read from a scanned page by local OCR ({h.OcrModel}); may contain recognition errors.");
            sb.AppendLine($"Relevance: {h.Score:F4}" + (h.Similarity is { } s ? $", similarity {s:F2}" : ""));
            sb.AppendLine("```");
            sb.AppendLine(h.Content);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "list_documents", ReadOnly = true)]
    [Description("List the documents in the user's RAG index with their collection, type, size and status.")]
    public async Task<string> ListDocuments(
        [Description("Only list this collection. Omit to list everything.")] string? collection = null,
        CancellationToken ct = default)
    {
        var q = db.Documents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(collection)) q = q.Where(d => d.Collection == collection);
        var docs = await q.OrderBy(d => d.Collection).ThenBy(d => d.Name)
            .Select(d => new { d.Id, d.Collection, d.Name, d.ContentType, d.Language, d.SizeBytes, d.ChunkCount, d.Status, d.UpdatedAt, d.OcrPages, d.PageCount })
            .ToListAsync(ct);
        if (docs.Count == 0)
            return collection is null ? "The index is empty." : $"No documents in collection \"{collection}\".";

        var sb = new StringBuilder($"{docs.Count} documents:\n\n| collection | name | type | size | chunks | status | updated | id |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var d in docs)
            sb.AppendLine($"| {d.Collection} | {d.Name} | {d.Language ?? d.ContentType}{(d.OcrPages > 0 ? $" (OCR {d.OcrPages}/{d.PageCount} pages)" : "")} | {FormatSize(d.SizeBytes)} | {d.ChunkCount} | {d.Status} | {d.UpdatedAt:yyyy-MM-dd} | {d.Id} |");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("Return the full extracted text of one indexed document, by name or id. Large documents are truncated.")]
    public async Task<string> GetDocument(
        [Description("Document name (e.g. \"setup.md\") or id from list_documents.")] string name_or_id,
        [Description("Collection, needed only if the same name exists in several collections.")] string? collection = null,
        CancellationToken ct = default)
    {
        var q = db.Documents.AsNoTracking();
        q = Guid.TryParse(name_or_id, out var id)
            ? q.Where(d => d.Id == id)
            : q.Where(d => d.Name == name_or_id);
        if (!string.IsNullOrWhiteSpace(collection)) q = q.Where(d => d.Collection == collection);

        var matches = await q.Take(5).ToListAsync(ct);
        if (matches.Count == 0) return $"No document named \"{name_or_id}\".";
        if (matches.Count > 1)
            return $"\"{name_or_id}\" exists in several collections ({string.Join(", ", matches.Select(m => m.Collection))}); pass collection.";

        var doc = matches[0];
        if (doc.Status != DocumentStatus.Ready || doc.Text is null)
            return $"{doc.Collection}/{doc.Name} is {doc.Status.Replace('_', ' ')}" + (doc.Error is null ? "." : $": {doc.Error}");

        var max = options.Value.MaxDocumentChars;
        var text = doc.Text.Length > max
            ? doc.Text[..max] + $"\n\n[truncated: showing {max:N0} of {doc.Text.Length:N0} characters; use search_documents for specific parts]"
            : doc.Text;
        var ocrNote = doc.OcrPages > 0
            ? $"Note: {doc.OcrPages} of {doc.PageCount} pages were read by local OCR ({doc.OcrModel}) and may contain recognition errors.\n\n"
            : "";
        return $"# {doc.Collection}/{doc.Name}\n\n{ocrNote}{text}";
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };
}
