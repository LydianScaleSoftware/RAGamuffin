using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;
using static Ragamuffin.Api.DocumentEndpoints;

namespace Ragamuffin.Api;

/// <summary>
/// The Conversions page: documents Claude made from another one (usually Markdown transcribed from a PDF), grouped by
/// the document they came from, each downloadable in full; all of a source's conversions download together as a zip.
/// </summary>
public static class ConversionEndpoints
{
    public static void MapConversionApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/conversions", async (RagDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Documents.AsNoTracking().Where(d => d.SourceDocumentId != null)
                .Select(d => new
                {
                    d.Id, d.Name, d.Collection, d.ContentType, d.SizeBytes, d.ChunkCount, d.ChunkSource, d.Status,
                    d.CreatedAt, d.UpdatedAt,
                    SourceId = d.SourceDocumentId!.Value,
                    Spans = d.Chunks.Where(k => k.Page != null).Select(k => new PageSpan(k.Page!.Value, k.PageEnd)).ToList(),
                })
                .ToListAsync(ct);
            var sourceIds = rows.Select(r => r.SourceId).Distinct().ToList();
            var sources = await db.Documents.AsNoTracking().Where(d => sourceIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Name, d.Collection, d.ContentType, d.PageCount, d.SizeBytes })
                .ToDictionaryAsync(d => d.Id, ct);

            return rows.GroupBy(r => r.SourceId)
                .Select(g =>
                {
                    var source = sources.GetValueOrDefault(g.Key);
                    var covered = PageRanges(g.SelectMany(r => r.Spans));
                    return new
                    {
                        Source = source,
                        // Pages of the source the conversions stand in for together; null = the whole document.
                        CoveredPages = covered,
                        CoveredCount = g.SelectMany(r => r.Spans)
                            .SelectMany(s => Enumerable.Range(s.Page, Math.Max(1, (s.PageEnd ?? s.Page) - s.Page + 1)))
                            .Distinct().Count(),
                        Conversions = g.OrderBy(r => r.Name).Select(r => new
                        {
                            r.Id, r.Name, r.Collection, r.ContentType, r.SizeBytes, r.ChunkCount, r.Status, r.CreatedAt, r.UpdatedAt,
                            Claudomatics = r.ChunkSource == ChunkSources.Claude,
                            Pages = PageRanges(r.Spans),
                        }),
                    };
                })
                .OrderBy(g => g.Source?.Name);
        });

        // Every conversion of one source document, as their full text in a zip, named as they are in the library.
        api.MapGet("/conversions/{sourceId:guid}/zip", async (Guid sourceId, RagDbContext db, CancellationToken ct) =>
        {
            var sourceName = await db.Documents.Where(d => d.Id == sourceId).Select(d => d.Name).SingleOrDefaultAsync(ct);
            var docs = await db.Documents.AsNoTracking().Where(d => d.SourceDocumentId == sourceId && d.Text != null)
                .OrderBy(d => d.Name).Select(d => new { d.Name, d.Text }).ToListAsync(ct);
            if (sourceName is null || docs.Count == 0) return Results.NotFound();

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var doc in docs)
                {
                    var entry = zip.CreateEntry(doc.Name.Replace('\\', '/').TrimStart('/'), CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(doc.Text!), ct);
                }
            var file = $"{Path.GetFileNameWithoutExtension(sourceName)} conversions.zip";
            return Results.File(buffer.ToArray(), "application/zip", file);
        });
    }
}
