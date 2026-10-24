using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Layout;
using Ragamuffin.Ocr;

namespace Ragamuffin.Api;

/// <summary><c>Scope</c> is "missing" (pages without text), "suspect" (plus pages that look like poor OCR) or "all".</summary>
public sealed record RunOcrRequest(string? Scope);

/// <summary><c>Redo</c> re-analyzes pages that were analyzed before.</summary>
public sealed record RunLayoutRequest(bool Redo);

/// <summary>
/// Queue analysis for many documents: the given ids, or every PDF in <c>Collection</c> (all collections when both are
/// empty). <c>Ocr</c> OCRs pages without text; <c>Layout</c> lays out pages with embedded text (<c>RedoLayout</c> includes
/// pages analyzed before). <c>DryRun</c> only reports what would be queued.
/// </summary>
public sealed record BatchRequest(List<Guid>? DocumentIds, string? Collection, bool Ocr, bool Layout, bool RedoLayout, bool DryRun);

public sealed record BatchItem(Guid Id, string Name, string? OcrScope, string? LayoutMode, int EstimatedPages);

public static class OcrEndpoints
{
    public static void MapOcrApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ocr", (OcrEngine ocr, CancellationToken ct) => ocr.GetAvailabilityAsync(ct));
        api.MapGet("/layout", (LayoutClient layout, CancellationToken ct) => layout.GetAvailabilityAsync(ct));

        api.MapPost("/documents/{id:guid}/ocr", async (Guid id, RunOcrRequest? req, RagDbContext db, OcrEngine ocr,
            IngestQueue queue, CancellationToken ct) =>
        {
            var scope = OcrScopes.Parse(req?.Scope ?? "missing");
            if (scope is null) return Results.BadRequest("scope must be \"problems\", \"missing\", \"suspect\" or \"all\"");

            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.ContentType != "pdf") return Results.BadRequest("OCR applies to PDFs only.");

            var availability = await ocr.GetAvailabilityAsync(ct);
            if (!availability.Available) return Results.Conflict(availability.Reason);

            return await QueueAsync(db, queue, id, scope, null, ct)
                ? Results.Accepted()
                : Results.Conflict("The document is already being indexed or analyzed.");
        });

        api.MapPost("/documents/{id:guid}/layout", async (Guid id, RunLayoutRequest? req, RagDbContext db, LayoutClient layout,
            IngestQueue queue, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.ContentType != "pdf") return Results.BadRequest("Layout analysis applies to PDFs only.");

            var availability = await layout.GetAvailabilityAsync(ct);
            if (!availability.Available) return Results.Conflict(availability.Reason);

            return await QueueAsync(db, queue, id, null, req?.Redo == true ? LayoutMode.Redo : LayoutMode.New, ct)
                ? Results.Accepted()
                : Results.Conflict("The document is already being indexed or analyzed.");
        });

        api.MapPost("/documents/{id:guid}/ocr/cancel", async (Guid id, RagDbContext db, OcrCancellation cancellation, CancellationToken ct) =>
        {
            if (!await db.Documents.AnyAsync(d => d.Id == id && d.Status == DocumentStatus.Ocr, ct))
                return Results.Conflict("No analysis is queued or running for this document.");
            cancellation.Cancel(id);
            return Results.Accepted();
        });

        // Switch OCR'd pages back to the PDF's own text (layout-arranged when available) where it has some;
        // pages without embedded text keep their OCR.
        api.MapPost("/documents/{id:guid}/revert-ocr", async (Guid id, RagDbContext db, IngestQueue queue, CancellationToken ct) =>
        {
            var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.Status is DocumentStatus.Pending or DocumentStatus.Ocr)
                return Results.Conflict("The document is already being indexed or analyzed.");

            var pages = await db.Pages.Where(p => p.DocumentId == id && p.Source == PageSource.Ocr).ToListAsync(ct);
            var reverted = 0;
            foreach (var page in pages.Where(p => PdfPages.HasUsefulText(p.EmbeddedText)))
            {
                page.Source = page.EmbeddedSource;
                reverted++;
            }
            if (reverted == 0) return Results.Conflict("No OCR'd pages have embedded text to revert to.");

            doc.Status = DocumentStatus.Pending;
            doc.Error = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(id, ct);
            return Results.Ok(new { reverted });
        });

        api.MapPost("/batch", async (BatchRequest req, RagDbContext db, IngestQueue queue, OcrEngine ocr, LayoutClient layout,
            FileStore files, CancellationToken ct) =>
        {
            if (!req.Ocr && !req.Layout) return Results.BadRequest("Choose OCR, layout analysis or both.");
            var ocrOk = !req.Ocr || (await ocr.GetAvailabilityAsync(ct)).Available;
            var layoutOk = !req.Layout || (await layout.GetAvailabilityAsync(ct)).Available;

            var docs = db.Documents.AsNoTracking().Where(d => d.ContentType == "pdf");
            if (req.DocumentIds is { Count: > 0 } ids) docs = docs.Where(d => ids.Contains(d.Id));
            else if (!string.IsNullOrWhiteSpace(req.Collection)) docs = docs.Where(d => d.Collection == req.Collection);
            var candidates = await docs.OrderBy(d => d.Collection).ThenBy(d => d.Name).ToListAsync(ct);

            var plan = new List<BatchItem>();
            var busy = 0;
            foreach (var d in candidates)
            {
                if (d.Status is DocumentStatus.Pending or DocumentStatus.Ocr) { busy++; continue; }

                // Documents that failed before page analysis existed have no page counts yet; read them from the file.
                var pageCount = d.PageCount;
                if (pageCount == 0 && files.Exists(d.Id))
                {
                    try { pageCount = OcrEngine.PageCount(await files.ReadAsync(d.Id, ct)); }
                    catch { pageCount = 0; }
                }
                var emptyPages = d.PageCount == 0 && d.Status == DocumentStatus.NeedsOcr ? pageCount : d.EmptyPages;
                var textPages = Math.Max(0, pageCount - emptyPages);

                OcrScope? scope = req.Ocr && ocrOk && (emptyPages > 0 || d.Status == DocumentStatus.NeedsOcr) ? OcrScope.MissingText : null;
                var layoutPages = req.RedoLayout ? textPages : Math.Max(0, textPages - d.LayoutPages);
                LayoutMode? mode = req.Layout && layoutOk && layoutPages > 0 ? (req.RedoLayout ? LayoutMode.Redo : LayoutMode.New) : null;
                if (scope is null && mode is null) continue;

                plan.Add(new BatchItem(d.Id, d.Name, OcrScopes.Name(scope), LayoutModes.Name(mode),
                    (scope is null ? 0 : emptyPages) + (mode is null ? 0 : layoutPages)));
            }

            var queued = 0;
            if (!req.DryRun)
                foreach (var item in plan)
                    if (await QueueAsync(db, queue, item.Id, OcrScopes.Parse(item.OcrScope), LayoutModes.Parse(item.LayoutMode), ct))
                        queued++;

            return Results.Ok(new
            {
                dryRun = req.DryRun,
                documents = plan,
                queued,
                skippedBusy = busy,
                totalPages = plan.Sum(p => p.EstimatedPages),
                ocrUnavailable = !ocrOk,
                layoutUnavailable = !layoutOk,
            });
        });

        api.MapGet("/queue", async (RagDbContext db, CancellationToken ct) =>
        {
            var items = await db.Documents.AsNoTracking().Where(d => d.Status == DocumentStatus.Ocr)
                .OrderBy(d => d.OcrRequestedAt)
                .Select(d => new { d.Id, d.Name, d.Collection, d.OcrScope, d.LayoutMode, d.ProgressDone, d.ProgressTotal, d.PageCount, d.EmptyPages, d.LayoutPages, d.OcrRequestedAt })
                .ToListAsync(ct);
            var running = items.FirstOrDefault(i => i.ProgressTotal != null);
            var queued = items.Where(i => i.ProgressTotal == null).ToList();
            return new
            {
                running,
                queued,
                queuedPages = queued.Sum(q => (q.OcrScope != null ? q.EmptyPages : 0) + (q.LayoutMode != null ? Math.Max(0, q.PageCount - q.EmptyPages) : 0)),
            };
        });

        api.MapPost("/queue/cancel", async (RagDbContext db, OcrCancellation cancellation, CancellationToken ct) =>
        {
            var ids = await db.Documents.Where(d => d.Status == DocumentStatus.Ocr).Select(d => d.Id).ToListAsync(ct);
            foreach (var id in ids) cancellation.Cancel(id);
            return Results.Ok(new { cancelled = ids.Count });
        });
    }

    /// <summary>Marks the document as queued for analysis and enqueues it, unless it's already busy.</summary>
    static async Task<bool> QueueAsync(RagDbContext db, IngestQueue queue, Guid id, OcrScope? scope, LayoutMode? mode, CancellationToken ct)
    {
        var updated = await db.Documents
            .Where(d => d.Id == id && d.Status != DocumentStatus.Pending && d.Status != DocumentStatus.Ocr)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DocumentStatus.Ocr)
                .SetProperty(d => d.Error, (string?)null)
                .SetProperty(d => d.ProgressDone, (int?)null)
                .SetProperty(d => d.ProgressTotal, (int?)null)
                .SetProperty(d => d.OcrScope, OcrScopes.Name(scope))
                .SetProperty(d => d.LayoutMode, LayoutModes.Name(mode))
                .SetProperty(d => d.OcrRequestedAt, DateTimeOffset.UtcNow)
                .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (updated == 0) return false;
        await queue.EnqueueAsync(new IngestJob(id, JobKind.Analyze, scope, mode), ct);
        return true;
    }
}
