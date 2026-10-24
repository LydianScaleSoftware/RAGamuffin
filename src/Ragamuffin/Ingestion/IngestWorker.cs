using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Layout;
using Ragamuffin.Ocr;

namespace Ragamuffin.Ingestion;

public enum JobKind
{
    /// <summary>Extract, chunk and embed (fast lane).</summary>
    Index,
    /// <summary>OCR and/or layout analysis of PDF pages, then re-chunk (slow lane).</summary>
    Analyze,
}

public enum LayoutMode
{
    /// <summary>Lay out pages with embedded text that haven't been analyzed yet.</summary>
    New,
    /// <summary>Lay out every page with embedded text again.</summary>
    Redo,
}

public static class LayoutModes
{
    public static LayoutMode? Parse(string? name) => name switch { "new" => LayoutMode.New, "redo" => LayoutMode.Redo, _ => null };

    public static string? Name(LayoutMode? mode) => mode switch { LayoutMode.New => "new", LayoutMode.Redo => "redo", _ => null };
}

/// <summary>An index job, or an analysis job with an optional OCR scope and an optional layout mode.</summary>
public sealed record IngestJob(Guid DocumentId, JobKind Kind = JobKind.Index, OcrScope? Scope = null, LayoutMode? Layout = null);

/// <summary>
/// Two independent lanes: indexing (uploads, re-indexes) and analysis (OCR, layout). A long analysis run never holds
/// up indexing.
/// </summary>
public sealed class IngestQueue
{
    readonly Channel<IngestJob> _index = Channel.CreateUnbounded<IngestJob>();
    readonly Channel<IngestJob> _analyze = Channel.CreateUnbounded<IngestJob>();

    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => EnqueueAsync(new IngestJob(id), ct);

    public ValueTask EnqueueAsync(IngestJob job, CancellationToken ct = default) => Lane(job.Kind).Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<IngestJob> ReadAllAsync(JobKind lane, CancellationToken ct) => Lane(lane).Reader.ReadAllAsync(ct);

    Channel<IngestJob> Lane(JobKind kind) => kind == JobKind.Analyze ? _analyze : _index;
}

/// <summary>
/// Runs the indexing lane and the OCR lane side by side, each one job at a time. Index jobs extract, chunk and
/// embed a document; for PDFs the text comes from the per-page table, so OCR'd pages survive re-indexing. OCR
/// jobs transcribe selected PDF pages with the vision model and then rebuild the document's chunks. The API
/// never queues the same document in both lanes at once: each job only proceeds while the document is in the
/// status its lane owns (pending for indexing, ocr for OCR).
/// </summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopes,
    IngestQueue queue,
    OllamaEmbedder embedder,
    OcrEngine ocr,
    LayoutClient layout,
    PageAnalyzer analyzer,
    OcrCancellation cancellation,
    FileStore files,
    IOptions<RagOptions> options,
    ILogger<IngestWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resume work interrupted by a restart. OCR runs continue where they stopped: finished pages are saved
        // as they complete, and pages OCR'd since the run was requested are skipped.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            // Running job first (it has progress), then the rest in the order they were queued.
            var interrupted = await db.Documents.Where(d => d.Status == DocumentStatus.Pending || d.Status == DocumentStatus.Ocr)
                .OrderBy(d => d.ProgressTotal == null).ThenBy(d => d.OcrRequestedAt).ThenBy(d => d.UpdatedAt)
                .Select(d => new { d.Id, d.Status, d.OcrScope, d.LayoutMode }).ToListAsync(stoppingToken);
            foreach (var d in interrupted)
            {
                if (d.Status == DocumentStatus.Ocr)
                    await queue.EnqueueAsync(new IngestJob(d.Id, JobKind.Analyze, OcrScopes.Parse(d.OcrScope), LayoutModes.Parse(d.LayoutMode)), stoppingToken);
                else
                    await queue.EnqueueAsync(d.Id, stoppingToken);
            }
            if (interrupted.Count > 0)
                log.LogInformation("Resuming {Count} interrupted jobs", interrupted.Count);
        }

        await Task.WhenAll(RunLaneAsync(JobKind.Index, stoppingToken), RunLaneAsync(JobKind.Analyze, stoppingToken));
    }

    async Task RunLaneAsync(JobKind lane, CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(lane, stoppingToken))
        {
            try
            {
                if (lane == JobKind.Analyze) await AnalyzeAsync(job, stoppingToken);
                else await IndexAsync(job.DocumentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The document was replaced or deleted while this job ran; the newer action owns it now.
                log.LogInformation("{Kind} job for document {Id} was superseded", lane, job.DocumentId);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Kind} job for document {Id} failed", lane, job.DocumentId);
                await MarkFailedAsync(job.DocumentId, lane == JobKind.Analyze ? DocumentStatus.Ocr : DocumentStatus.Pending,
                    ex is ExtractionException or OcrException or LayoutException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    async Task IndexAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null || doc.Status != DocumentStatus.Pending) return;

        var bytes = await ReadOriginalAsync(id, ct);
        if (doc.ContentType == "pdf")
        {
            await SyncPagesAsync(db, id, PdfPages.Extract(bytes), ct);
            await RebuildFromPagesAsync(db, doc, DocumentStatus.Pending, notice: null, ct);
            return;
        }

        // Chunks Claude chose keep their boundaries; only if they no longer fit the text does the chunker take over.
        if (doc.ChunkSource == ChunkSources.Claude)
        {
            if (await scope.ServiceProvider.GetRequiredService<ClaudeIndexer>().RebuildAsync(doc, ct)) return;
            doc.ChunkSource = ChunkSources.Auto;
        }

        var extracted = TextExtractor.Extract(doc.Name, bytes);
        var drafts = NewChunker().Chunk(extracted);
        if (drafts.Count == 0)
            throw new ExtractionException("No text found in document.");
        await CommitAsync(db, doc, extracted, drafts, ocrPages: new HashSet<int>(), DocumentStatus.Pending, d =>
        {
            d.Status = DocumentStatus.Ready;
            d.Error = null;
        }, ct);
    }

    async Task AnalyzeAsync(IngestJob job, CancellationToken stopping)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == job.DocumentId, stopping);
        if (doc is null || doc.Status != DocumentStatus.Ocr) return;

        using var cts = cancellation.Start(doc.Id, stopping);
        try
        {
            var bytes = await ReadOriginalAsync(doc.Id, stopping);
            var pages = await SyncPagesAsync(db, doc.Id, PdfPages.Extract(bytes), stopping);
            var notices = new List<string>();

            // On resume, pages finished earlier in this job are already done.
            var requested = doc.OcrRequestedAt ?? DateTimeOffset.MaxValue;
            var finished = pages.Where(p => p.OcrAt >= requested || p.LayoutAt >= requested).Select(p => p.Page).ToHashSet();

            var ocrTargets = job.Scope is { } s ? PdfPages.SelectForOcr(pages, s) : [];
            var layoutTargets = job.Layout is { } mode ? PdfPages.SelectForLayout(pages, mode).Except(ocrTargets).ToList() : [];

            var ocrStatus = await ocr.GetAvailabilityAsync(stopping);
            var layoutStatus = await layout.GetAvailabilityAsync(stopping);
            if (doc.OcrRequestedAt is null)
            {
                // Started by a version that didn't record the job, so it can't be resumed faithfully.
                notices.Add("OCR was interrupted by a server restart; finished pages were kept. Use Re-index… to run it again.");
                ocrTargets = layoutTargets = [];
            }
            if (ocrTargets.Count > 0 && !ocrStatus.Available)
            {
                notices.Add("OCR not run: " + ocrStatus.Reason);
                ocrTargets = [];
            }
            if (layoutTargets.Count > 0 && !layoutStatus.Available)
            {
                notices.Add("Layout analysis not run: " + layoutStatus.Reason);
                layoutTargets = [];
            }
            var total = ocrTargets.Count + layoutTargets.Count;
            var limit = options.Value.Ocr.MaxPages;
            if (total > limit)
            {
                notices.Add($"Not run: {total} pages exceeds the limit of {limit} (Rag:Ocr:MaxPages).");
                ocrTargets = layoutTargets = [];
            }
            if (job.Scope is not null && job.Layout is null && ocrTargets.Count == 0 && notices.Count == 0)
                notices.Add("OCR not run: no pages matched.");

            if (ocrTargets.Count + layoutTargets.Count > 0)
            {
                var work = ocrTargets.Select(p => (Page: p, Ocr: true))
                    .Concat(layoutTargets.Select(p => (Page: p, Ocr: false)))
                    .OrderBy(w => w.Page).ToList();
                var notice = await ProcessPagesAsync(db, doc, bytes, pages, work, finished, layoutStatus.Available, cts.Token, stopping);
                if (notice is not null) notices.Add(notice);
            }

            await RebuildFromPagesAsync(db, doc, DocumentStatus.Ocr, notices.Count > 0 ? string.Join(" ", notices) : null, stopping);
        }
        finally
        {
            cancellation.Finish(doc.Id);
        }
    }

    /// <summary>
    /// OCRs or lays out each page in turn, saving each as it finishes. OCR uses layout regions when the layout service
    /// is available, and whole-page OCR otherwise. Returns a notice if the run stopped early or something was skipped.
    /// </summary>
    async Task<string?> ProcessPagesAsync(RagDbContext db, Document doc, byte[] bytes, List<DocumentPage> pages,
        List<(int Page, bool Ocr)> work, IReadOnlySet<int> alreadyDone, bool useLayout, CancellationToken cancel, CancellationToken stopping)
    {
        var byNumber = pages.ToDictionary(p => p.Page);
        var model = options.Value.Ocr.Model;
        // Pages finished before a restart no longer match the job's page selection (they have text now), so count them
        // separately to keep progress continuous across resumes.
        var remaining = work.Where(w => !alreadyDone.Contains(w.Page)).ToList();
        var total = remaining.Count + alreadyDone.Count;
        var done = alreadyDone.Count;
        var blank = new List<int>();
        var rotatedText = new List<int>();
        var wholePage = 0;
        var fallbackRegions = 0;
        await SetProgressAsync(db, doc.Id, done, total, stopping);
        var started = DateTimeOffset.UtcNow;

        foreach (var (number, isOcr) in remaining)
        {
            var page = byNumber[number];
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (isOcr)
                {
                    string text;
                    PageLayoutResult? result = null;
                    if (useLayout)
                    {
                        try { result = await analyzer.OcrPageAsync(bytes, number - 1, cancel); }
                        catch (LayoutException ex)
                        {
                            log.LogWarning("Layout analysis failed for page {Page} of {Name}: {Error}; using whole-page OCR", number, doc.Name, ex.Message);
                        }
                    }
                    if (result is not null)
                    {
                        text = result.Text;
                        fallbackRegions += result.FallbackRegions;
                    }
                    else
                    {
                        wholePage++;
                        text = await ocr.TranscribeAsync(ocr.RenderPage(bytes, number - 1), cancel);
                    }

                    page.OcrText = text;
                    page.OcrModel = model;
                    page.OcrAt = DateTimeOffset.UtcNow;
                    page.LayoutJson = result?.RegionsJson;
                    page.LayoutAngle = result?.Angle;
                    page.LayoutAt = result is null ? null : page.OcrAt;
                    // Text Claude checked stays in use; the new OCR becomes what removing Claude's text falls back to.
                    if (!PdfPages.HasUsefulText(text)) blank.Add(number);
                    else if (page.Source == PageSource.Claude) page.SourceBeforeClaude = PageSource.Ocr;
                    else page.Source = PageSource.Ocr;
                }
                else
                {
                    var result = await analyzer.ArrangeEmbeddedAsync(bytes, number - 1, cancel);
                    if (result is null) rotatedText.Add(number);
                    else
                    {
                        page.LayoutText = result.Text;
                        page.LayoutJson = result.RegionsJson;
                        page.LayoutAngle = result.Angle;
                        page.LayoutAt = DateTimeOffset.UtcNow;
                        if (page.Source == PageSource.Embedded && PdfPages.HasUsefulText(result.Text))
                            page.Source = PageSource.Layout;
                    }
                }
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                return done == 0
                    ? "Cancelled before any page finished."
                    : $"Cancelled after {done} of {total} pages; finished pages were kept.";
            }
            catch (Exception ex) when (ex is OcrException or LayoutException)
            {
                return $"Stopped at page {number}: {ex.Message} ({done} of {total} pages finished; they were kept.)";
            }

            await db.SaveChangesAsync(stopping);
            await SetProgressAsync(db, doc.Id, ++done, total, stopping);
        }

        log.LogInformation("Analyzed {Pages} pages of {Name} in {Seconds:F0}s", done, doc.Name, (DateTimeOffset.UtcNow - started).TotalSeconds);
        var notes = new List<string>();
        if (blank.Count > 0) notes.Add($"OCR found no text on page{(blank.Count > 1 ? "s" : "")} {string.Join(", ", blank)}.");
        if (wholePage > 0) notes.Add($"{wholePage} page{(wholePage > 1 ? "s were" : " was")} OCR'd without layout analysis (layout service failed).");
        if (fallbackRegions > 0) notes.Add($"{fallbackRegions} region{(fallbackRegions > 1 ? "s" : "")} used the layout service's own reading because glm-ocr failed on {(fallbackRegions > 1 ? "them" : "it")}.");
        if (rotatedText.Count > 0) notes.Add($"Layout skipped for rotated page{(rotatedText.Count > 1 ? "s" : "")} {string.Join(", ", rotatedText)}; they keep their embedded text.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    Task SetProgressAsync(RagDbContext db, Guid id, int done, int total, CancellationToken ct) =>
        db.Documents.Where(d => d.Id == id && d.Status == DocumentStatus.Ocr)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ProgressDone, done).SetProperty(d => d.ProgressTotal, total), ct);

    /// <summary>Updates the per-page table with freshly extracted embedded text, keeping OCR text and sources.</summary>
    static async Task<List<DocumentPage>> SyncPagesAsync(RagDbContext db, Guid documentId, List<EmbeddedPage> embedded, CancellationToken ct)
    {
        var existing = await db.Pages.Where(p => p.DocumentId == documentId).ToDictionaryAsync(p => p.Page, ct);
        foreach (var e in embedded)
        {
            if (existing.TryGetValue(e.Page, out var page))
            {
                page.EmbeddedText = e.Text;
                page.SuspectReason = e.SuspectReason;
            }
            else
            {
                db.Pages.Add(existing[e.Page] = new DocumentPage
                {
                    DocumentId = documentId,
                    Page = e.Page,
                    EmbeddedText = e.Text,
                    SuspectReason = e.SuspectReason,
                });
            }
        }
        foreach (var stale in existing.Values.Where(p => p.Page > embedded.Count).ToList())
        {
            db.Pages.Remove(stale);
            existing.Remove(stale.Page);
        }
        await db.SaveChangesAsync(ct);
        return existing.Values.OrderBy(p => p.Page).ToList();
    }

    /// <summary>Rebuilds a PDF's chunks from its pages' current text and settles its status.</summary>
    async Task RebuildFromPagesAsync(RagDbContext db, Document doc, string expectedStatus, string? notice, CancellationToken ct)
    {
        var pages = await db.Pages.AsNoTracking().Where(p => p.DocumentId == doc.Id).OrderBy(p => p.Page).ToListAsync(ct);
        var extracted = PdfPages.ToExtracted(pages);
        var ocrPages = pages.Where(p => p.Source == PageSource.Ocr && PdfPages.HasUsefulText(p.OcrText)).Select(p => p.Page).ToHashSet();
        var drafts = extracted.Pages!.Count == 0 ? [] : NewChunker().Chunk(extracted);

        var committed = await CommitAsync(db, doc, extracted, drafts, ocrPages, expectedStatus, d =>
        {
            ApplyPageStats(d, pages);
            d.Status = drafts.Count == 0 ? DocumentStatus.NeedsOcr : DocumentStatus.Ready;
            d.Error = notice ?? (drafts.Count == 0 ? "No embedded text. Run OCR to read the scanned pages." : null);
            if (drafts.Count == 0) d.Text = null;
            d.ProgressDone = d.ProgressTotal = null;
            d.OcrScope = null;
            d.LayoutMode = null;
            d.OcrRequestedAt = null;
        }, ct);

        // Optional: queue OCR of text-less pages and/or layout analysis straight after import.
        if (committed && expectedStatus == DocumentStatus.Pending)
        {
            var opt = options.Value;
            OcrScope? autoOcr = opt.Ocr.AutoOnImport && doc.EmptyPages > 0 && (await ocr.GetAvailabilityAsync(ct)).Available
                ? OcrScope.MissingText : null;
            LayoutMode? autoLayout = opt.Layout.AutoOnImport && doc.PageCount > doc.EmptyPages && (await layout.GetAvailabilityAsync(ct)).Available
                ? LayoutMode.New : null;
            if (autoOcr is not null || autoLayout is not null)
            {
                await db.Documents.Where(d => d.Id == doc.Id && d.Status != DocumentStatus.Pending && d.Status != DocumentStatus.Ocr)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, DocumentStatus.Ocr)
                        .SetProperty(d => d.OcrScope, OcrScopes.Name(autoOcr))
                        .SetProperty(d => d.LayoutMode, LayoutModes.Name(autoLayout))
                        .SetProperty(d => d.OcrRequestedAt, DateTimeOffset.UtcNow), ct);
                await queue.EnqueueAsync(new IngestJob(doc.Id, JobKind.Analyze, autoOcr, autoLayout), ct);
            }
        }
    }

    static void ApplyPageStats(Document doc, List<DocumentPage> pages)
    {
        var ocrPages = pages.Where(p => p.Source == PageSource.Ocr && PdfPages.HasUsefulText(p.OcrText)).ToList();
        doc.PageCount = pages.Count;
        doc.OcrPages = ocrPages.Count;
        doc.EmptyPages = pages.Count(p => !PdfPages.HasUsefulText(p.EffectiveText));
        doc.SuspectPages = pages.Count(p => p.SuspectReason != null && p.Source != PageSource.Ocr);
        doc.LayoutPages = pages.Count(p => p.LayoutAt != null && (p.Source == PageSource.Layout || (p.Source == PageSource.Ocr && p.LayoutJson != null)));
        doc.OcrModel = ocrPages.OrderByDescending(p => p.OcrAt).FirstOrDefault()?.OcrModel;
        doc.OcrAt = ocrPages.Max(p => p.OcrAt);
    }

    /// <summary>
    /// Embeds the chunks and swaps them in, unless the document changed status or content meanwhile
    /// (re-uploaded, deleted, or another job started). Returns whether anything was written.
    /// </summary>
    async Task<bool> CommitAsync(RagDbContext db, Document doc, ExtractedDocument extracted, List<ChunkDraft> drafts,
        IReadOnlySet<int> ocrPages, string expectedStatus, Action<Document> finish, CancellationToken ct)
    {
        var sha = doc.Sha256;
        var started = DateTimeOffset.UtcNow;
        var vectors = drafts.Count == 0
            ? []
            : await embedder.EmbedDocumentsAsync(drafts.Select(c => Chunker.EmbeddingText(doc.Name, extracted, c)).ToList(), ct);
        if (vectors.Count > 0 && vectors[0].Length != RagDbContext.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"Embedding model returned {vectors[0].Length} dimensions; the database expects {RagDbContext.EmbeddingDimensions}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Entry(doc).ReloadAsync(ct);
        if (db.Entry(doc).State == EntityState.Detached || doc.Sha256 != sha || doc.Status != expectedStatus)
            return false;

        await db.Chunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
        db.Chunks.AddRange(drafts.Select((c, i) => new Chunk
        {
            DocumentId = doc.Id,
            Ordinal = i,
            Content = c.Content,
            HeadingPath = c.HeadingPath,
            StartLine = c.StartLine,
            EndLine = c.EndLine,
            Page = c.Page,
            Ocr = c.Page is { } p && ocrPages.Contains(p),
            Embedding = new Vector(vectors[i]),
        }));
        doc.ContentType = extracted.ContentType;
        doc.Language = extracted.Language;
        doc.Text = extracted.Text;
        doc.ChunkCount = drafts.Count;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        finish(doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Indexed {Collection}/{Name}: {Chunks} chunks in {Seconds:F1}s ({Status})",
            doc.Collection, doc.Name, drafts.Count, (DateTimeOffset.UtcNow - started).TotalSeconds, doc.Status);
        return true;
    }

    async Task<byte[]> ReadOriginalAsync(Guid id, CancellationToken ct)
    {
        if (!files.Exists(id))
            throw new ExtractionException("Original file is missing from the file store; upload it again.");
        return await files.ReadAsync(id, ct);
    }

    Chunker NewChunker()
    {
        var opt = options.Value;
        return new Chunker(new ChunkerSettings(opt.ChunkTargetChars, opt.ChunkMaxChars, opt.ChunkOverlapChars));
    }

    /// <summary>Marks the document failed, but only if it is still in the status the failed job's lane owns.</summary>
    async Task MarkFailedAsync(Guid id, string laneStatus, string error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            await db.Documents.Where(d => d.Id == id && d.Status == laneStatus)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Failed)
                    .SetProperty(d => d.Error, error)
                    .SetProperty(d => d.ProgressDone, (int?)null)
                    .SetProperty(d => d.ProgressTotal, (int?)null)
                    .SetProperty(d => d.OcrScope, (string?)null)
                    .SetProperty(d => d.LayoutMode, (string?)null)
                    .SetProperty(d => d.OcrRequestedAt, (DateTimeOffset?)null)
                    .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not mark document {Id} as failed", id);
        }
    }
}
