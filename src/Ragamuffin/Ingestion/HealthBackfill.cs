using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;
using Ragamuffin.Ocr;

namespace Ragamuffin.Ingestion;

/// <summary>
/// Brings documents indexed before page health existed up to date, once: checks their text-less pages for blankness,
/// recounts their page states, and drops the old page-problem notices (the health dialog lists those pages now).
/// </summary>
public sealed class HealthBackfill(IServiceScopeFactory scopes, ILogger<HealthBackfill> log) : BackgroundService
{
    const string DoneKey = "health.backfill";
    const string Version = "1";

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<FileStore>();
            if (await db.Settings.AnyAsync(s => s.Key == DoneKey && s.Value == Version, stopping)) return;

            var ids = await db.Documents.Where(d => d.ContentType == "pdf" &&
                    (d.Status == DocumentStatus.Ready || d.Status == DocumentStatus.NeedsOcr))
                .Select(d => d.Id).ToListAsync(stopping);
            var blanks = 0;
            foreach (var id in ids)
            {
                db.ChangeTracker.Clear();
                var doc = await db.Documents.SingleAsync(d => d.Id == id, stopping);
                var pages = await db.Pages.Where(p => p.DocumentId == id).OrderBy(p => p.Page).ToListAsync(stopping);
                var unchecked_ = pages.Where(p => p.Blank is null && !PdfPages.HasUsefulText(p.EffectiveText)).ToList();
                if (unchecked_.Count > 0 && pages.Any(p => PdfPages.HasUsefulText(p.EffectiveText)) && files.Exists(id))
                {
                    var bytes = await files.ReadAsync(id, stopping);
                    foreach (var p in unchecked_)
                    {
                        try { p.Blank = BlankPage.IsBlank(bytes, p.Page - 1); }
                        catch (Exception ex) { log.LogWarning("Couldn't check page {Page} of {Name} for blankness: {Error}", p.Page, doc.Name, ex.Message); }
                        if (p.Blank == true) blanks++;
                    }
                }
                IngestWorker.ApplyPageStats(doc, pages);
                if (doc.Error?.Contains(" had OCR or layout problems (") == true) doc.Error = null;
                await db.SaveChangesAsync(stopping);
            }

            db.ChangeTracker.Clear();
            var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == DoneKey, stopping);
            if (setting is null) db.Settings.Add(new Setting { Key = DoneKey, Value = Version, UpdatedAt = DateTimeOffset.UtcNow });
            else { setting.Value = Version; setting.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(stopping);
            log.LogInformation("Page health: recounted {Count} PDFs; {Blank} text-less pages are blank", ids.Count, blanks);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Page health backfill failed; it runs again at the next start");
        }
    }
}
