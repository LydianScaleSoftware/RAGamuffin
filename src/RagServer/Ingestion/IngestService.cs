using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using RagServer.Data;
using RagServer.Embeddings;

namespace RagServer.Ingestion;

public sealed record UploadResult(string Name, string Collection, Guid? Id, string Outcome, string? Error = null);

/// <summary>Accepts uploads, stores originals, and queues documents for background indexing.</summary>
public sealed class IngestService(
    RagDbContext db,
    FileStore files,
    IngestQueue queue,
    IOptions<RagOptions> options)
{
    public async Task<UploadResult> UploadAsync(string name, string collection, byte[] bytes, CancellationToken ct)
    {
        name = name.Trim();
        collection = string.IsNullOrWhiteSpace(collection) ? "default" : collection.Trim();

        if (bytes.Length == 0)
            return new(name, collection, null, "rejected", "File is empty.");
        if (bytes.Length > options.Value.MaxUploadBytes)
            return new(name, collection, null, "rejected", "File is larger than the upload limit.");
        if (!TextExtractor.IsPdf(name) && TextExtractor.DecodeText(bytes) is null)
            return new(name, collection, null, "rejected", "Binary file; only text, markdown, source code and PDF are supported.");

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var now = DateTimeOffset.UtcNow;
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Collection == collection && d.Name == name, ct);

        if (doc is { Status: DocumentStatus.Ready } && doc.Sha256 == sha)
            return new(name, collection, doc.Id, "unchanged");

        var outcome = doc is null ? "added" : "replaced";
        if (doc is null)
        {
            doc = new Document
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                Collection = collection,
                ContentType = "text",
                Sha256 = sha,
                CreatedAt = now,
            };
            db.Documents.Add(doc);
        }

        (doc.ContentType, doc.Language) = TextExtractor.Classify(name);
        doc.Sha256 = sha;
        doc.SizeBytes = bytes.Length;
        doc.Status = DocumentStatus.Pending;
        doc.Error = null;
        doc.UpdatedAt = now;

        await files.SaveAsync(doc.Id, bytes, ct);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(doc.Id, ct);
        return new(name, collection, doc.Id, outcome);
    }
}

public sealed class FileStore
{
    readonly string _root;

    public FileStore(IOptions<RagOptions> options, IHostEnvironment env)
    {
        _root = Path.GetFullPath(options.Value.FilesDirectory, env.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    string PathFor(Guid id) => Path.Combine(_root, id.ToString("N"));

    public Task SaveAsync(Guid id, byte[] bytes, CancellationToken ct) => File.WriteAllBytesAsync(PathFor(id), bytes, ct);

    public Task<byte[]> ReadAsync(Guid id, CancellationToken ct) => File.ReadAllBytesAsync(PathFor(id), ct);

    public bool Exists(Guid id) => File.Exists(PathFor(id));

    public void Delete(Guid id)
    {
        if (File.Exists(PathFor(id))) File.Delete(PathFor(id));
    }
}

public sealed class IngestQueue
{
    readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => _channel.Writer.WriteAsync(id, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Extracts, chunks and embeds queued documents one at a time.</summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopes,
    IngestQueue queue,
    OllamaEmbedder embedder,
    FileStore files,
    IOptions<RagOptions> options,
    ILogger<IngestWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pick up anything left pending by a previous run.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var pending = await db.Documents.Where(d => d.Status == DocumentStatus.Pending)
                .Select(d => d.Id).ToListAsync(stoppingToken);
            foreach (var id in pending)
                await queue.EnqueueAsync(id, stoppingToken);
        }

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Indexing document {Id} failed", id);
                await MarkFailedAsync(id, ex is ExtractionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null || doc.Status != DocumentStatus.Pending) return;

        if (!files.Exists(id))
            throw new ExtractionException("Original file is missing from the file store; upload it again.");

        var sha = doc.Sha256;
        var started = DateTimeOffset.UtcNow;
        var extracted = TextExtractor.Extract(doc.Name, await files.ReadAsync(id, ct));
        var opt = options.Value;
        var chunker = new Chunker(new ChunkerSettings(opt.ChunkTargetChars, opt.ChunkMaxChars, opt.ChunkOverlapChars));
        var drafts = chunker.Chunk(extracted);
        if (drafts.Count == 0)
            throw new ExtractionException("No text found in document.");

        var vectors = await embedder.EmbedDocumentsAsync(
            drafts.Select(c => Chunker.EmbeddingText(doc.Name, extracted, c)).ToList(), ct);
        if (vectors[0].Length != RagDbContext.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"Embedding model returned {vectors[0].Length} dimensions; the database expects {RagDbContext.EmbeddingDimensions}.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The document may have been re-uploaded or deleted while we were embedding.
        await db.Entry(doc).ReloadAsync(ct);
        if (db.Entry(doc).State == EntityState.Detached || doc.Sha256 != sha || doc.Status != DocumentStatus.Pending)
            return;

        await db.Chunks.Where(c => c.DocumentId == id).ExecuteDeleteAsync(ct);
        db.Chunks.AddRange(drafts.Select((c, i) => new Chunk
        {
            DocumentId = id,
            Ordinal = i,
            Content = c.Content,
            HeadingPath = c.HeadingPath,
            StartLine = c.StartLine,
            EndLine = c.EndLine,
            Page = c.Page,
            Embedding = new Vector(vectors[i]),
        }));
        doc.ContentType = extracted.ContentType;
        doc.Language = extracted.Language;
        doc.Text = extracted.Text;
        doc.ChunkCount = drafts.Count;
        doc.Status = DocumentStatus.Ready;
        doc.Error = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Indexed {Collection}/{Name}: {Chunks} chunks in {Seconds:F1}s",
            doc.Collection, doc.Name, drafts.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    async Task MarkFailedAsync(Guid id, string error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            await db.Documents.Where(d => d.Id == id && d.Status == DocumentStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Failed)
                    .SetProperty(d => d.Error, error)
                    .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not mark document {Id} as failed", id);
        }
    }
}
