using Microsoft.EntityFrameworkCore;
using RagServer.Data;
using RagServer.Ingestion;
using RagServer.Ocr;

namespace RagServer.Api;

/// <summary><c>Scope</c> is "missing" (pages without text), "suspect" (plus pages that look like poor OCR) or "all".</summary>
public sealed record RunOcrRequest(string? Scope);

public static class OcrEndpoints
{
    public static void MapOcrApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ocr", (OcrEngine ocr, CancellationToken ct) => ocr.GetAvailabilityAsync(ct));

        api.MapPost("/documents/{id:guid}/ocr", async (Guid id, RunOcrRequest? req, RagDbContext db, OcrEngine ocr,
            IngestQueue queue, CancellationToken ct) =>
        {
            var scopeName = req?.Scope ?? "missing";
            var scope = OcrScopes.Parse(scopeName);
            if (scope is null) return Results.BadRequest("scope must be \"missing\", \"suspect\" or \"all\"");

            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.ContentType != "pdf") return Results.BadRequest("OCR applies to PDFs only.");

            var availability = await ocr.GetAvailabilityAsync(ct);
            if (!availability.Available) return Results.Conflict(availability.Reason);

            var started = await db.Documents
                .Where(d => d.Id == id && d.Status != DocumentStatus.Pending && d.Status != DocumentStatus.Ocr)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Ocr)
                    .SetProperty(d => d.Error, (string?)null)
                    .SetProperty(d => d.ProgressDone, (int?)null)
                    .SetProperty(d => d.ProgressTotal, (int?)null)
                    .SetProperty(d => d.OcrScope, scopeName)
                    .SetProperty(d => d.OcrRequestedAt, DateTimeOffset.UtcNow)
                    .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            if (started == 0) return Results.Conflict("The document is already being indexed or OCR'd.");

            await queue.EnqueueAsync(new IngestJob(id, JobKind.Ocr, scope.Value), ct);
            return Results.Accepted();
        });

        api.MapPost("/documents/{id:guid}/ocr/cancel", async (Guid id, RagDbContext db, OcrCancellation cancellation, CancellationToken ct) =>
        {
            if (!await db.Documents.AnyAsync(d => d.Id == id && d.Status == DocumentStatus.Ocr, ct))
                return Results.Conflict("No OCR run for this document.");
            cancellation.Cancel(id);
            return Results.Accepted();
        });

        // Switch OCR'd pages back to the PDF's own text where it has some; pages without embedded text keep their OCR.
        api.MapPost("/documents/{id:guid}/revert-ocr", async (Guid id, RagDbContext db, IngestQueue queue, CancellationToken ct) =>
        {
            var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.Status is DocumentStatus.Pending or DocumentStatus.Ocr)
                return Results.Conflict("The document is already being indexed or OCR'd.");

            var pages = await db.Pages.Where(p => p.DocumentId == id && p.Source == PageSource.Ocr).ToListAsync(ct);
            var reverted = 0;
            foreach (var page in pages.Where(p => PdfPages.HasUsefulText(p.EmbeddedText)))
            {
                page.Source = PageSource.Embedded;
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
    }
}
