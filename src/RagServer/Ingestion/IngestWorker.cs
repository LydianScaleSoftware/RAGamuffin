using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using RagServer.Data;
using RagServer.Embeddings;
using RagServer.Ocr;

namespace RagServer.Ingestion;

public enum JobKind { Index, Ocr }

public sealed record IngestJob(Guid DocumentId, JobKind Kind = JobKind.Index, OcrScope Scope = OcrScope.MissingText);

/// <summary>
/// Two independent lanes: indexing (uploads, re-indexes) and OCR. A long OCR run never holds up indexing.
/// </summary>
public sealed class IngestQueue
{
    readonly Channel<IngestJob> _index = Channel.CreateUnbounded<IngestJob>();
    readonly Channel<IngestJob> _ocr = Channel.CreateUnbounded<IngestJob>();

    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => EnqueueAsync(new IngestJob(id), ct);

    public ValueTask EnqueueAsync(IngestJob job, CancellationToken ct = default) => Lane(job.Kind).Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<IngestJob> ReadAllAsync(JobKind lane, CancellationToken ct) => Lane(lane).Reader.ReadAllAsync(ct);

    Channel<IngestJob> Lane(JobKind kind) => kind == JobKind.Ocr ? _ocr : _index;
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
            var interrupted = await db.Documents.Where(d => d.Status == DocumentStatus.Pending || d.Status == DocumentStatus.Ocr)
                .Select(d => new { d.Id, d.Status, d.OcrScope }).ToListAsync(stoppingToken);
            foreach (var d in interrupted)
            {
                if (d.Status == DocumentStatus.Ocr)
                    await queue.EnqueueAsync(new IngestJob(d.Id, JobKind.Ocr, OcrScopes.Parse(d.OcrScope) ?? OcrScope.MissingText), stoppingToken);
                else
                    await queue.EnqueueAsync(d.Id, stoppingToken);
            }
            if (interrupted.Count > 0)
                log.LogInformation("Resuming {Count} interrupted jobs", interrupted.Count);
        }

        await Task.WhenAll(RunLaneAsync(JobKind.Index, stoppingToken), RunLaneAsync(JobKind.Ocr, stoppingToken));
    }

    async Task RunLaneAsync(JobKind lane, CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(lane, stoppingToken))
        {
            try
            {
                if (lane == JobKind.Ocr) await OcrAsync(job, stoppingToken);
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
                await MarkFailedAsync(job.DocumentId, lane == JobKind.Ocr ? DocumentStatus.Ocr : DocumentStatus.Pending,
                    ex is ExtractionException or OcrException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
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

    async Task OcrAsync(IngestJob job, CancellationToken stopping)
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
            // On resume, pages finished earlier in this run are already done.
            var requested = doc.OcrRequestedAt ?? DateTimeOffset.MaxValue;
            var finished = pages.Where(p => p.OcrAt >= requested).Select(p => p.Page).ToHashSet();
            var targets = PdfPages.SelectForOcr(pages, job.Scope).Union(finished).Order().ToList();
            var opt = options.Value.Ocr;
            string? notice = null;

            var availability = await ocr.GetAvailabilityAsync(stopping);
            if (doc.OcrRequestedAt is null)
                // Started by a version that didn't record the run's scope, so it can't be resumed faithfully.
                notice = "OCR was interrupted by a server restart; finished pages were kept. Use Re-index… to run it again.";
            else if (!availability.Available)
                notice = "OCR not run: " + availability.Reason;
            else if (targets.Count > opt.MaxPages)
                notice = $"OCR not run: {targets.Count} pages exceeds the limit of {opt.MaxPages} (Rag:Ocr:MaxPages).";
            else if (targets.Count == 0)
                notice = "OCR not run: no pages matched.";
            else
                notice = await TranscribePagesAsync(db, doc, bytes, pages, targets, finished, opt.Model, cts.Token, stopping);

            await RebuildFromPagesAsync(db, doc, DocumentStatus.Ocr, notice, stopping);
        }
        finally
        {
            cancellation.Finish(doc.Id);
        }
    }

    /// <summary>OCRs the target pages, saving each as it finishes. Returns a notice if the run stopped early or found nothing.</summary>
    async Task<string?> TranscribePagesAsync(RagDbContext db, Document doc, byte[] bytes, List<DocumentPage> pages,
        List<int> targets, IReadOnlySet<int> alreadyDone, string model, CancellationToken cancel, CancellationToken stopping)
    {
        var byNumber = pages.ToDictionary(p => p.Page);
        var done = targets.Count(alreadyDone.Contains);
        var blank = new List<int>();
        await SetProgressAsync(db, doc.Id, done, targets.Count, stopping);
        var started = DateTimeOffset.UtcNow;

        foreach (var number in targets.Where(n => !alreadyDone.Contains(n)))
        {
            string text;
            try
            {
                cancel.ThrowIfCancellationRequested();
                var png = ocr.RenderPage(bytes, number - 1);
                text = await ocr.TranscribeAsync(png, cancel);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                return done == 0
                    ? "OCR cancelled before any page finished."
                    : $"OCR cancelled after {done} of {targets.Count} pages; finished pages were kept.";
            }
            catch (OcrException ex)
            {
                return $"OCR stopped at page {number}: {ex.Message} ({done} of {targets.Count} pages finished; they were kept.)";
            }

            var page = byNumber[number];
            page.OcrText = text;
            page.OcrModel = model;
            page.OcrAt = DateTimeOffset.UtcNow;
            if (PdfPages.HasUsefulText(text)) page.Source = PageSource.Ocr;
            else blank.Add(number);
            await db.SaveChangesAsync(stopping);
            await SetProgressAsync(db, doc.Id, ++done, targets.Count, stopping);
        }

        log.LogInformation("OCR'd {Pages} pages of {Name} with {Model} in {Seconds:F0}s",
            done, doc.Name, model, (DateTimeOffset.UtcNow - started).TotalSeconds);
        return blank.Count == 0 ? null : $"OCR found no text on page{(blank.Count > 1 ? "s" : "")} {string.Join(", ", blank)}.";
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
            d.OcrRequestedAt = null;
        }, ct);

        // Optional: OCR text-less pages straight after import.
        if (committed && expectedStatus == DocumentStatus.Pending && doc.EmptyPages > 0 && options.Value.Ocr.AutoOnImport
            && (await ocr.GetAvailabilityAsync(ct)).Available)
        {
            await db.Documents.Where(d => d.Id == doc.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Ocr)
                    .SetProperty(d => d.OcrScope, "missing")
                    .SetProperty(d => d.OcrRequestedAt, DateTimeOffset.UtcNow), ct);
            await queue.EnqueueAsync(new IngestJob(doc.Id, JobKind.Ocr, OcrScope.MissingText), ct);
        }
    }

    static void ApplyPageStats(Document doc, List<DocumentPage> pages)
    {
        var ocrPages = pages.Where(p => p.Source == PageSource.Ocr && PdfPages.HasUsefulText(p.OcrText)).ToList();
        doc.PageCount = pages.Count;
        doc.OcrPages = ocrPages.Count;
        doc.EmptyPages = pages.Count(p => !PdfPages.HasUsefulText(p.EffectiveText));
        doc.SuspectPages = pages.Count(p => p.SuspectReason != null && p.Source == PageSource.Embedded);
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
                    .SetProperty(d => d.OcrRequestedAt, (DateTimeOffset?)null)
                    .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not mark document {Id} as failed", id);
        }
    }
}
