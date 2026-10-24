using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Ragamuffin.Collections;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Ocr;

namespace Ragamuffin.Ingestion;

public sealed record UploadResult(string Name, string Collection, Guid? Id, string Outcome, string? Error = null);

/// <summary>Accepts uploads, stores originals, and queues documents for background indexing.</summary>
public sealed class IngestService(
    RagDbContext db,
    FileStore files,
    IngestQueue queue,
    OcrCancellation ocrCancellation,
    CollectionService collections,
    IOptions<RagOptions> options)
{
    /// <param name="allowBinary">
    /// Accept a binary file and index it as a hex dump (import_file); the web upload leaves this off and rejects binaries.
    /// </param>
    public async Task<UploadResult> UploadAsync(string name, string collection, byte[] bytes, CancellationToken ct, bool allowBinary = false)
    {
        name = name.Trim();
        collection = CollectionRules.Normalize(collection);
        if (collection.Length == 0 || CollectionRules.IsDefault(collection)) collection = Collection.Default;
        else if (CollectionRules.ValidateName(collection) is { } nameError)
            return new(name, collection, null, "rejected", "Invalid collection: " + nameError);

        if (bytes.Length == 0)
            return new(name, collection, null, "rejected", "File is empty.");
        if (bytes.Length > options.Value.MaxUploadBytes)
            return new(name, collection, null, "rejected", "File is larger than the upload limit.");
        var binary = !TextExtractor.IsPdf(name) && TextExtractor.DecodeText(bytes) is null;
        if (binary && !allowBinary)
            return new(name, collection, null, "rejected", "Binary file; only text, markdown, source code and PDF are supported.");
        if (binary && bytes.Length > options.Value.Mcp.MaxBinaryBytes)
            return new(name, collection, null, "rejected", $"Binary file larger than the {options.Value.Mcp.MaxBinaryBytes:N0}-byte limit for encoded files.");

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var now = DateTimeOffset.UtcNow;
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Collection == collection && d.Name == name, ct);

        if (doc is { Status: DocumentStatus.Ready or DocumentStatus.NeedsOcr or DocumentStatus.Ocr } && doc.Sha256 == sha)
            return new(name, collection, doc.Id, "unchanged");

        // New content invalidates the old file's pages and any OCR of them, including a run in progress.
        if (doc is not null && doc.Sha256 != sha)
        {
            if (doc.Status == DocumentStatus.Ocr) ocrCancellation.Cancel(doc.Id);
            await db.Pages.Where(p => p.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            doc.OcrScope = null;
            doc.OcrRequestedAt = null;
        }

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

        (doc.ContentType, doc.Language) = binary ? ("binary", null) : TextExtractor.Classify(name);
        doc.Encoding = binary ? Encodings.Hex : null;
        doc.Sha256 = sha;
        doc.SizeBytes = bytes.Length;
        doc.Status = DocumentStatus.Pending;
        doc.Error = null;
        doc.ProgressDone = doc.ProgressTotal = null;
        doc.UpdatedAt = now;

        await collections.EnsureExistsAsync(collection, ct);
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

