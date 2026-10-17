using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Collections;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Ingestion;
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
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.OcrModel, d.OcrAt,
                    d.LayoutPages, d.ProgressDone, d.ProgressTotal, d.OcrScope, d.LayoutMode,
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
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.OcrModel, d.OcrAt,
                    d.LayoutPages, d.ProgressDone, d.ProgressTotal, d.OcrScope, d.LayoutMode,
                    Origin = StatsService.Classify(d.ContentType, mix).ToString(),
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
                    d.PageCount, d.OcrPages, d.EmptyPages, d.SuspectPages, d.OcrModel, d.OcrAt,
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
                .Select(c => new { c.Id, c.Ordinal, c.HeadingPath, c.StartLine, c.EndLine, c.Page, c.Ocr, c.Content })
                .ToListAsync(ct));

        api.MapGet("/documents/{id:guid}/file", async (Guid id, RagDbContext db, FileStore files, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null || !files.Exists(id)) return Results.NotFound();
            var type = doc.ContentType == "pdf" ? "application/pdf" : "text/plain; charset=utf-8";
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

        api.MapGet("/stats", (StatsService stats, CancellationToken ct) => stats.BuildAsync(ct));

        api.MapGet("/collections", (CollectionService collections, CancellationToken ct) => collections.ListAsync(ct));

        api.MapPost("/collections", async (CreateCollectionsRequest req, CollectionService collections, CancellationToken ct) =>
            Results.Ok(await collections.CreateAsync(req.Names ?? [], ct)));

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

        api.MapGet("/search", async (string q, int? k, string? collection, SearchService search, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Results.BadRequest("q is required")
                : Results.Ok(await search.SearchAsync(q, k ?? 8, collection, ct)));

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
}

public sealed record CreateCollectionsRequest(List<string>? Names);

public sealed record RenameCollectionsRequest(List<CollectionRename>? Renames);

/// <summary><c>Documents</c> is "move" (to the default collection) or "delete".</summary>
public sealed record RemoveCollectionsRequest(List<string>? Names, string? Documents);

public sealed record UpdateCollectionDetailsRequest(List<CollectionDetails>? Items);

public sealed record SuggestCollectionRequest(string Name);
