using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;

namespace Ragamuffin.Api;

public sealed record PagesMuteRequest(List<int>? Pages, bool Muted);
public sealed record PagesVerifyRequest(List<int>? Pages, bool Verified);
public sealed record DocumentVerifyRequest(bool Verified, string? Note);
public sealed record NoticeMuteRequest(bool Muted);

/// <summary>
/// A PDF's page health for the web UI's health dialog: which pages have problems or notes, which are verified or
/// blank, and the actions that settle them (mute or unmute, mark pages or the whole document correct, mute the
/// notice). None of these change any text, so nothing is re-indexed; the document's counts are recounted.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/documents/{id:guid}/health", async (Guid id, RagDbContext db, CancellationToken ct) =>
        {
            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            var pages = await db.Pages.AsNoTracking().Where(p => p.DocumentId == id).OrderBy(p => p.Page).ToListAsync(ct);
            var rows = pages.Select(p => new { Page = p, State = PageHealth.StateOf(p) }).ToList();
            return Results.Ok(new
            {
                doc.Id, doc.Name, doc.ContentType, doc.PageCount, doc.Status,
                Summary = PageHealth.Summary(doc),
                doc.ProblemPages, doc.NotePages, doc.MutedPages, doc.VerifiedPages, doc.BlankPages,
                UnverifiedPages = rows.Count(r => r.State == PageState.Unverified),
                ClaudePages = pages.Count(p => p.Source == PageSource.Claude),
                UserVerifiedPages = pages.Count(p => p.VerifiedAt != null && p.Source != PageSource.Claude),
                OcrLayerPages = pages.Count(p => p.SuspectReason != null && p.SuspectReason != PageHealth.GarbledReason &&
                                                 p.Source is PageSource.Embedded or PageSource.Layout),
                Verified = doc.VerifiedAt is null ? null : new { At = doc.VerifiedAt, By = doc.VerifiedBy, Note = doc.VerifiedNote },
                Notice = doc.Error, doc.NoticeMuted,
                // Pages worth listing one by one; unverified and verified pages are only counted.
                Pages = rows.Where(r => r.State is PageState.Problem or PageState.Note or PageState.Muted or PageState.Blank)
                    .Select(r => new
                    {
                        r.Page.Page,
                        State = r.State.ToString().ToLowerInvariant(),
                        Text = PageHealth.ProblemOf(r.Page) ?? r.Page.Issue ?? (r.State == PageState.Blank ? "Blank page." : null),
                        Kind = PageHealth.ProblemOf(r.Page) is not null ? IssueKinds.Problem : IssueKinds.Note,
                        r.Page.Source,
                    }),
            });
        });

        api.MapPost("/documents/{id:guid}/pages/mute", async (Guid id, PagesMuteRequest req, RagDbContext db, CancellationToken ct) =>
            await ChangePagesAsync(db, id, req.Pages, p =>
            {
                // Only a page with something to mute: a problem (recorded or detected) or a note.
                if (PageHealth.ProblemOf(p) is null && p.Issue is null) return false;
                p.IssueMuted = req.Muted;
                return true;
            }, ct));

        api.MapPost("/documents/{id:guid}/pages/verify", async (Guid id, PagesVerifyRequest req, RagDbContext db, CancellationToken ct) =>
            await ChangePagesAsync(db, id, req.Pages, p =>
            {
                if (req.Verified && !PdfPages.HasUsefulText(p.EffectiveText)) return false; // nothing to vouch for
                p.VerifiedAt = req.Verified ? DateTimeOffset.UtcNow : null;
                return true;
            }, ct));

        api.MapPost("/documents/{id:guid}/verify", async (Guid id, DocumentVerifyRequest req, RagDbContext db, CancellationToken ct) =>
        {
            var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.ContentType != "pdf") return Results.BadRequest("Only PDFs have page health.");
            doc.VerifiedAt = req.Verified ? DateTimeOffset.UtcNow : null;
            doc.VerifiedBy = req.Verified ? "user" : null;
            doc.VerifiedNote = req.Verified ? (string.IsNullOrWhiteSpace(req.Note) ? "marked correct in the web UI" : req.Note.Trim()) : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { summary = PageHealth.Summary(doc) });
        });

        api.MapPost("/documents/{id:guid}/notice/mute", async (Guid id, NoticeMuteRequest req, RagDbContext db, CancellationToken ct) =>
        {
            var updated = await db.Documents.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.NoticeMuted, req.Muted), ct);
            return updated == 0 ? Results.NotFound() : Results.Ok();
        });
    }

    /// <summary>Applies a change to the given pages (all pages when none are given) and recounts the document.</summary>
    static async Task<IResult> ChangePagesAsync(RagDbContext db, Guid id, List<int>? numbers, Func<DocumentPage, bool> change, CancellationToken ct)
    {
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return Results.NotFound();
        if (doc.Status == DocumentStatus.Ocr) return Results.Conflict("The document is being analyzed; try again when it finishes.");
        var pages = await db.Pages.Where(p => p.DocumentId == id).OrderBy(p => p.Page).ToListAsync(ct);
        var targets = numbers is { Count: > 0 } ? pages.Where(p => numbers.Contains(p.Page)) : pages;
        var changed = targets.Count(change);
        IngestWorker.ApplyPageStats(doc, pages);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { changed, summary = PageHealth.Summary(doc) });
    }
}
