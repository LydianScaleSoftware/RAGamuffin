using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Ingestion;
using Ragamuffin.Ocr;

namespace Ragamuffin.Api;

public sealed record UpdateClaudomaticRequest(string? Title, string? Summary);

/// <summary>
/// The Claudomatics page: chunks whose boundaries Claude chose (documents added with add_document or re-chunked with
/// set_chunks). Browse them by date and source, read one in full, fix its title or summary, delete it, export its
/// document, and look at the PDF page it came from.
/// </summary>
public static class ClaudomaticEndpoints
{
    const int SnippetChars = 240;

    public static void MapClaudomaticApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/claudomatics", async (RagDbContext db, string? q, string? collection, int? limit, CancellationToken ct) =>
        {
            var chunks = db.Chunks.AsNoTracking().Where(c => c.Document.ChunkSource == ChunkSources.Claude);
            if (!string.IsNullOrWhiteSpace(collection)) chunks = chunks.Where(c => c.Document.Collection == collection);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                chunks = chunks.Where(c => EF.Functions.ILike(c.Content, like) || EF.Functions.ILike(c.Title ?? "", like) ||
                                           EF.Functions.ILike(c.Summary ?? "", like) || EF.Functions.ILike(c.Symbol ?? "", like) ||
                                           EF.Functions.ILike(c.Document.Name, like));
            }
            var total = await chunks.CountAsync(ct);
            var items = await chunks
                .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Document.Name).ThenBy(c => c.Ordinal)
                .Take(Math.Clamp(limit ?? 500, 1, 2000))
                .Select(c => new
                {
                    c.Id, c.DocumentId, DocumentName = c.Document.Name, c.Document.Collection, c.Document.Encoding,
                    c.Document.SourceDocumentId,
                    SourceName = db.Documents.Where(s => s.Id == c.Document.SourceDocumentId).Select(s => s.Name).FirstOrDefault(),
                    c.Ordinal, c.Title, c.HeadingPath, c.Summary, c.Kind, c.Symbol, c.StartLine, c.EndLine, c.Page, c.PageEnd,
                    c.CreatedAt, Chars = c.Content.Length,
                    Snippet = c.Content.Substring(0, Math.Min(c.Content.Length, SnippetChars)),
                })
                .ToListAsync(ct);
            return Results.Ok(new { total, items });
        });

        api.MapGet("/chunks/{id:long}", async (long id, RagDbContext db, CancellationToken ct) =>
        {
            var c = await db.Chunks.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new
                {
                    x.Id, x.DocumentId, DocumentName = x.Document.Name, x.Document.Collection, x.Document.ChunkSource,
                    x.Document.Encoding, x.Document.SourceDocumentId,
                    SourceName = db.Documents.Where(s => s.Id == x.Document.SourceDocumentId).Select(s => s.Name).FirstOrDefault(),
                    x.Ordinal, x.Title, x.HeadingPath, x.Summary, x.Kind, x.Symbol, x.StartLine, x.EndLine, x.Page, x.PageEnd,
                    x.CreatedAt, x.Content,
                })
                .SingleOrDefaultAsync(ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        // Fix a Claudomatic's title or summary; the chunk is re-embedded, since both are part of what's embedded.
        api.MapPut("/chunks/{id:long}", async (long id, UpdateClaudomaticRequest req, RagDbContext db, OllamaEmbedder embedder,
            IOptions<RagOptions> options, CancellationToken ct) =>
        {
            var chunk = await db.Chunks.Include(c => c.Document).SingleOrDefaultAsync(c => c.Id == id, ct);
            if (chunk is null) return Results.NotFound();
            if (chunk.Document.ChunkSource != ChunkSources.Claude)
                return Results.BadRequest(new { errors = new[] { "Only Claudomatics (chunks Claude chose) can be edited." } });
            var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim();
            var summary = string.IsNullOrWhiteSpace(req.Summary) ? null : req.Summary.Trim();
            if (title is { Length: > ChunkPlan.MaxTitleLength })
                return Results.BadRequest(new { errors = new[] { $"The title is longer than {ChunkPlan.MaxTitleLength} characters." } });
            if (summary is { Length: > 2000 })
                return Results.BadRequest(new { errors = new[] { "The summary is longer than 2,000 characters." } });

            chunk.Title = title;
            chunk.Summary = summary;
            var vector = (await embedder.EmbedDocumentsAsync([Chunker.EmbeddingText(chunk.Document.Name, chunk.Document.Language,
                chunk.HeadingPath, chunk.Page, chunk.Title, chunk.Symbol, chunk.Summary, chunk.Content, options.Value.EmbeddingMaxChars)], ct))[0];
            chunk.Embedding = new Vector(vector);
            chunk.Document.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { chunk.Id, chunk.Title, chunk.Summary });
        });

        // Remove one Claudomatic. Its lines are no longer searchable until Claude re-chunks the document.
        api.MapDelete("/chunks/{id:long}", async (long id, RagDbContext db, CancellationToken ct) =>
        {
            var chunk = await db.Chunks.Include(c => c.Document).SingleOrDefaultAsync(c => c.Id == id, ct);
            if (chunk is null) return Results.NotFound();
            if (chunk.Document.ChunkSource != ChunkSources.Claude)
                return Results.BadRequest(new { errors = new[] { "Only Claudomatics can be deleted one at a time; re-index the document instead." } });
            db.Chunks.Remove(chunk);
            chunk.Document.ChunkCount = Math.Max(0, chunk.Document.ChunkCount - 1);
            chunk.Document.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // When each of a document's Claudomatics was written, so deleting the document can say which days it reaches.
        api.MapGet("/documents/{id:guid}/claudomatic-dates", async (Guid id, RagDbContext db, CancellationToken ct) =>
            await db.Chunks.AsNoTracking()
                .Where(c => c.DocumentId == id && c.Document.ChunkSource == ChunkSources.Claude)
                .Select(c => c.CreatedAt).ToListAsync(ct));

        // Download a document's text as a file: a Markdown conversion as .md, a binary's hex dump as .hex.txt.
        // (GET /api/documents/{id}/file returns the original bytes.)
        api.MapGet("/documents/{id:guid}/export", async (Guid id, RagDbContext db, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ContentType, d.Encoding, d.Text }).SingleOrDefaultAsync(ct);
            if (doc?.Text is null) return Results.NotFound();
            var file = Path.GetFileName(doc.Name);
            if (doc.Encoding is not null) file += $".{doc.Encoding}.txt";
            else if (doc.ContentType == "pdf") file = Path.ChangeExtension(file, ".txt");
            var type = doc.ContentType == "markdown" ? "text/markdown; charset=utf-8" : "text/plain; charset=utf-8";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(doc.Text), type, file);
        });

        // One PDF page as an image (the same rendering get_page gives Claude).
        api.MapGet("/documents/{id:guid}/pages/{page:int}/image", async (Guid id, int page, RagDbContext db, FileStore files,
            OcrEngine ocr, CancellationToken ct) =>
        {
            // Renders from the stored PDF itself, so it works even if the document's own indexing failed.
            var type = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.ContentType).SingleOrDefaultAsync(ct);
            if (type != "pdf" || page < 1 || !files.Exists(id)) return Results.NotFound();
            try
            {
                return Results.File(ocr.RenderPage(await files.ReadAsync(id, ct), page - 1), "image/png");
            }
            catch (Exception) when (!ct.IsCancellationRequested) // e.g. a page past the end of the PDF
            {
                return Results.NotFound();
            }
        });
    }
}
