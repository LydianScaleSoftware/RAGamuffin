using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Collections;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Ingestion;
using Ragamuffin.Mcp;
using Ragamuffin.Ocr;
using Ragamuffin.Search;
using Ragamuffin.Stats;

namespace Ragamuffin.Api;

public static class DocumentEndpoints
{
    public static void MapRagApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/documents", async (RagDbContext db, string? collection, string? q, CancellationToken ct) =>
        {
            var docs = db.Documents.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(collection)) docs = docs.Where(d => d.Collection == collection);
            if (!string.IsNullOrWhiteSpace(q)) docs = docs.Where(d => EF.Functions.ILike(d.Name, $"%{q}%"));
            var mixes = await StatsService.PageMixesAsync(db, ct);
            var rows = await docs.OrderBy(d => d.Collection).ThenBy(d => d.Name)
                .Select(d => new
                {
                    d.Id, d.Name, d.Collection, d.ContentType, d.Language, d.SizeBytes,
                    d.ChunkCount, d.Status, d.Error, d.CreatedAt, d.UpdatedAt,
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.IssuePages, d.OcrModel, d.OcrAt,
                    d.ProblemPages, d.NotePages, d.MutedPages, d.VerifiedPages, d.BlankPages, d.VerifiedAt, d.VerifiedBy, d.VerifiedNote, d.NoticeMuted,
                    d.LayoutPages, d.ProgressDone, d.ProgressTotal, d.OcrScope, d.LayoutMode,
                    d.ChunkSource, d.Encoding,
                    ClaudePages = db.Pages.Count(p => p.DocumentId == d.Id && p.Source == PageSource.Claude),
                    SourceName = db.Documents.Where(o => o.Id == d.SourceDocumentId).Select(o => o.Name).FirstOrDefault(),
                    // Page spans of a conversion's chunks: the PDF pages it stands in for in search.
                    SourceSpans = db.Chunks.Where(k => d.SourceDocumentId != null && k.DocumentId == d.Id && k.Page != null)
                        .Select(k => new PageSpan(k.Page!.Value, k.PageEnd)).ToList(),
                    ConvertedTo = db.Documents.Where(o => o.SourceDocumentId == d.Id).OrderBy(o => o.Name)
                        .Select(o => new
                        {
                            o.Name,
                            Spans = o.Chunks.Where(k => k.Page != null).Select(k => new PageSpan(k.Page!.Value, k.PageEnd)).ToList(),
                        }).ToList(),
                })
                .ToListAsync(ct);
            // Text origin and raw-embedded page counts match the Stats page, so its drill-down links filter exactly.
            return rows.Select(d =>
            {
                var mix = mixes.GetValueOrDefault(d.Id);
                return new
                {
                    d.Id, d.Name, d.Collection, d.ContentType, d.Language, d.SizeBytes,
                    d.ChunkCount, d.Status, d.Error, d.CreatedAt, d.UpdatedAt,
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.IssuePages, d.OcrModel, d.OcrAt,
                    d.ProblemPages, d.NotePages, d.MutedPages, d.VerifiedPages, d.BlankPages, d.VerifiedAt, d.VerifiedBy, d.VerifiedNote, d.NoticeMuted,
                    d.LayoutPages, d.ProgressDone, d.ProgressTotal, d.OcrScope, d.LayoutMode,
                    d.ChunkSource, d.Encoding, d.ClaudePages, d.SourceName,
                    SourcePages = d.SourceName is null ? null : PageRanges(d.SourceSpans),
                    ConvertedTo = d.ConvertedTo.Select(c => new { c.Name, Pages = PageRanges(c.Spans) }),
                    Origin = (d.Encoding is not null ? TextOrigin.EncodedBinary : StatsService.Classify(d.ContentType, mix)).ToString(),
                    RawEmbeddedPages = mix?.Embedded ?? 0,
                };
            });
        });

        api.MapPost("/documents", async ([FromForm] IFormFileCollection files, [FromForm] string? collection,
            IngestService ingest, CancellationToken ct) =>
        {
            var results = new List<UploadResult>();
            foreach (var file in files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                // Browsers send folder uploads as relative paths; keep them as the document name.
                var name = file.FileName.Replace('\\', '/');
                results.Add(await ingest.UploadAsync(name, collection ?? "default", ms.ToArray(), ct));
            }
            return Results.Ok(results);
        }).DisableAntiforgery();

        api.MapGet("/documents/{id:guid}", async (Guid id, RagDbContext db, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().Where(d => d.Id == id)
                .Select(d => new
                {
                    d.Id, d.Name, d.Collection, d.ContentType, d.Language, d.SizeBytes, d.Sha256,
                    d.ChunkCount, d.Status, d.Error, d.CreatedAt, d.UpdatedAt, d.Text,
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.IssuePages, d.OcrModel, d.OcrAt,
                    d.ProblemPages, d.NotePages, d.MutedPages, d.VerifiedPages, d.BlankPages, d.VerifiedAt, d.VerifiedBy, d.VerifiedNote, d.NoticeMuted,
                    d.LayoutPages, d.ProgressDone, d.ProgressTotal, d.OcrScope, d.LayoutMode,
                })
                .SingleOrDefaultAsync(ct);
            if (doc is null) return Results.NotFound();

            // OCR'd pages that also have embedded text can be reverted to it.
            var pages = await db.Pages.AsNoTracking().Where(p => p.DocumentId == id && p.Source == PageSource.Ocr)
                .Select(p => p.EmbeddedText).ToListAsync(ct);
            return Results.Ok(new { document = doc, revertablePages = pages.Count(PdfPages.HasUsefulText) });
        });

        api.MapGet("/documents/{id:guid}/chunks", async (Guid id, RagDbContext db, CancellationToken ct) =>
            await db.Chunks.AsNoTracking().Where(c => c.DocumentId == id).OrderBy(c => c.Ordinal)
                .Select(c => new { c.Id, c.Ordinal, c.HeadingPath, c.StartLine, c.EndLine, c.Page, c.PageEnd, c.Ocr, c.Title, c.Summary, c.Kind, c.Symbol, c.Content })
                .ToListAsync(ct));

        api.MapGet("/documents/{id:guid}/file", async (Guid id, RagDbContext db, FileStore files, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null || !files.Exists(id)) return Results.NotFound();
            var type = doc.ContentType switch
            {
                "pdf" => "application/pdf",
                "binary" => "application/octet-stream",
                "markdown" => "text/markdown; charset=utf-8",
                _ => "text/plain; charset=utf-8",
            };
            return Results.File(await files.ReadAsync(id, ct), type, Path.GetFileName(doc.Name));
        });

        api.MapDelete("/documents/{id:guid}", async (Guid id, RagDbContext db, FileStore files, OcrCancellation ocr, CancellationToken ct) =>
        {
            ocr.Cancel(id);
            var deleted = await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            files.Delete(id);
            return Results.NoContent();
        });

        api.MapPost("/documents/{id:guid}/reindex", async (Guid id, RagDbContext db, IngestQueue queue, CancellationToken ct) =>
        {
            var updated = await db.Documents.Where(d => d.Id == id && d.Status != DocumentStatus.Ocr)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Pending)
                    .SetProperty(d => d.Error, (string?)null)
                    .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            if (updated == 0)
                return await db.Documents.AnyAsync(d => d.Id == id, ct)
                    ? Results.Conflict("OCR is running; cancel it first.")
                    : Results.NotFound();
            await queue.EnqueueAsync(id, ct);
            return Results.Accepted();
        });

        // Rebuilds every document's chunks from its current text (PDF pages keep their OCR, layout and Claude text;
        // Claudomatics keep their boundaries). Used after changing the embedding model or chunking rules.
        api.MapPost("/reindex-all", async (RagDbContext db, IngestQueue queue, CancellationToken ct) =>
        {
            var ids = await db.Documents.Where(d => d.Status != DocumentStatus.Ocr && d.Status != DocumentStatus.Pending)
                .Select(d => d.Id).ToListAsync(ct);
            await db.Documents.Where(d => ids.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Pending).SetProperty(d => d.Error, (string?)null), ct);
            foreach (var id in ids) await queue.EnqueueAsync(id, ct);
            return Results.Ok(new { queued = ids.Count });
        });

        api.MapGet("/stats", (StatsService stats, CancellationToken ct) => stats.BuildAsync(ct));

        api.MapGet("/collections", (CollectionService collections, CancellationToken ct) => collections.ListAsync(ct));

        api.MapPost("/collections", async (CreateCollectionsRequest req, CollectionService collections, CancellationToken ct) =>
            Results.Ok(await collections.CreateAsync(req.Names ?? [], ct)));

        // Mute a collection (left out of search unless named explicitly) or unmute it.
        api.MapPost("/collections/mute", async (MuteCollectionRequest req, CollectionService collections, CancellationToken ct) =>
            await collections.SetMutedAsync(req.Name, req.Muted, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/collections/details", async (UpdateCollectionDetailsRequest req, CollectionService collections, CancellationToken ct) =>
        {
            var missing = await collections.UpdateDetailsAsync(req.Items ?? [], ct);
            return missing.Count == 0
                ? Results.NoContent()
                : Results.BadRequest(new { errors = missing.Select(m => $"Collection \"{m}\" does not exist.").ToList() });
        });

        api.MapPost("/collections/suggest", async (SuggestCollectionRequest req, CollectionSuggester suggester, CancellationToken ct) =>
        {
            try { return Results.Ok(await suggester.SuggestAsync(CollectionRules.Normalize(req.Name), ct)); }
            catch (SuggestionException ex) { return Results.BadRequest(new { errors = new[] { ex.Message } }); }
        });

        api.MapPost("/collections/rename", async (RenameCollectionsRequest req, CollectionService collections, CancellationToken ct) =>
        {
            var errors = await collections.RenameAsync(req.Renames ?? [], ct);
            return errors.Count == 0 ? Results.NoContent() : Results.BadRequest(new { errors });
        });

        api.MapPost("/collections/remove", async (RemoveCollectionsRequest req, CollectionService collections, CancellationToken ct) =>
        {
            var mode = req.Documents switch
            {
                "move" => RemoveMode.MoveToDefault,
                "delete" => RemoveMode.DeleteDocuments,
                _ => (RemoveMode?)null,
            };
            return mode is null
                ? Results.BadRequest(new { errors = new[] { "documents must be \"move\" or \"delete\"" } })
                : Results.Ok(await collections.RemoveAsync(req.Names ?? [], mode.Value, ct));
        });

        // Like MCP search: muted collections are left out unless includeMuted is set or the collection is named.
        api.MapGet("/search", async (string q, int? k, string? collection, bool? includeMuted, SearchService search, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Results.BadRequest("q is required")
                : Results.Ok(await search.SearchAsync(q, k ?? 8,
                    string.IsNullOrWhiteSpace(collection) ? new SearchScope(IncludeMuted: includeMuted == true) : SearchScope.For(collection), ct)));

        // The on/off switch for agents (MCP). Off makes every tool call a no-op that says so.
        api.MapGet("/settings/agents", (McpSwitch sw) => new { enabled = sw.Enabled, writes = sw.WritesEnabled, changedAt = sw.ChangedAt });
        api.MapPut("/settings/agents", async (McpSwitch sw, AgentsSetting body, CancellationToken ct) =>
        {
            await sw.SetAsync(body.Enabled, body.Writes, ct);
            return new { enabled = sw.Enabled, writes = sw.WritesEnabled, changedAt = sw.ChangedAt };
        });

        api.MapGet("/health", async (RagDbContext db, OllamaEmbedder embedder, IOptions<RagOptions> options, CancellationToken ct) =>
        {
            string database, ollama;
            int? dims = null;
            try
            {
                var docs = await db.Documents.CountAsync(ct);
                database = $"ok ({docs} documents)";
            }
            catch (Exception ex) { database = "error: " + ex.Message; }
            try
            {
                dims = (await embedder.EmbedQueryAsync("health check", ct)).Length;
                ollama = dims == RagDbContext.EmbeddingDimensions ? "ok" : $"dimension mismatch ({dims})";
            }
            catch (Exception ex) { ollama = "error: " + ex.Message; }

            var healthy = database.StartsWith("ok") && ollama == "ok";
            return Results.Json(new
            {
                status = healthy ? "ok" : "degraded",
                database,
                ollama,
                ollamaUrl = options.Value.OllamaUrl,
                model = options.Value.EmbeddingModel,
                dimensions = dims,
            }, statusCode: healthy ? 200 : 503);
        });
    }

    internal sealed record PageSpan(int Page, int? PageEnd);

    /// <summary>"156–174, 180" for the pages a set of chunk spans covers; null when none has a page (a conversion
    /// without page numbers stands in for the whole PDF).</summary>
    internal static string? PageRanges(IEnumerable<PageSpan> spans)
    {
        var pages = spans.SelectMany(s => Enumerable.Range(s.Page, Math.Max(1, (s.PageEnd ?? s.Page) - s.Page + 1)))
            .Distinct().Order().ToList();
        if (pages.Count == 0) return null;
        var parts = new List<string>();
        for (int i = 0, start = 0; i < pages.Count; i++)
        {
            if (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) continue;
            parts.Add(start == i ? $"{pages[i]}" : $"{pages[start]}–{pages[i]}");
            start = i + 1;
        }
        return string.Join(", ", parts);
    }
}

public sealed record CreateCollectionsRequest(List<string>? Names);

public sealed record RenameCollectionsRequest(List<CollectionRename>? Renames);

/// <summary><c>Documents</c> is "move" (to the default collection) or "delete".</summary>
public sealed record RemoveCollectionsRequest(List<string>? Names, string? Documents);

public sealed record UpdateCollectionDetailsRequest(List<CollectionDetails>? Items);

public sealed record SuggestCollectionRequest(string Name);

public sealed record AgentsSetting(bool? Enabled, bool? Writes);

public sealed record MuteCollectionRequest(string Name, bool Muted);
