using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Ragamuffin.Collections;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;

namespace Ragamuffin.Ingestion;

public sealed record ClaudeIndexResult(Document Document, string Outcome, int Chunks, List<LineGap> Gaps);

public sealed class ChunkPlanException(IReadOnlyList<string> errors) : Exception(string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Indexes text documents with chunk boundaries Claude chose, instead of the server's chunker: a Markdown conversion
/// of a manual, or source code cut at whole functions. The boundaries are kept on the chunks, so re-indexing
/// (after a model change, say) re-embeds the same chunks.
/// </summary>
public sealed class ClaudeIndexer(
    RagDbContext db,
    OllamaEmbedder embedder,
    FileStore files,
    CollectionService collections,
    IOptions<RagOptions> options,
    ILogger<ClaudeIndexer> log)
{
    RagOptions Opt => options.Value;

    /// <summary>Adds or replaces a document with the given text and chunks; embeds and stores them before returning.</summary>
    public async Task<ClaudeIndexResult> AddAsync(string name, string collection, byte[] bytes, IReadOnlyList<ChunkSpec> specs,
        Guid? sourceDocumentId, CancellationToken ct)
    {
        name = name.Trim();
        collection = CollectionRules.Normalize(collection);
        if (collection.Length == 0 || CollectionRules.IsDefault(collection)) collection = Collection.Default;
        else if (CollectionRules.ValidateName(collection) is { } nameError)
            throw new ChunkPlanException(["Invalid collection: " + nameError]);
        if (name.Length == 0) throw new ChunkPlanException(["The document needs a name."]);
        if (TextExtractor.IsPdf(name)) throw new ChunkPlanException(["add_document takes text (Markdown, code or plain text), not a PDF; use import_file for PDFs."]);

        var (text, encoding) = ToText(bytes);
        var plan = ChunkPlan.Validate(ChunkPlan.SplitLines(text), specs, Opt.Mcp.MaxChunkChars);
        if (!plan.Ok) throw new ChunkPlanException(plan.Errors);

        var doc = await db.Documents.SingleOrDefaultAsync(d => d.Collection == collection && d.Name == name, ct);
        if (doc is { Status: DocumentStatus.Ocr })
            throw new ChunkPlanException([$"\"{name}\" is being OCR'd; wait or cancel that first."]);
        if (doc is not null && doc.Id == sourceDocumentId)
            throw new ChunkPlanException(["A document can't be its own source."]);

        var now = DateTimeOffset.UtcNow;
        var outcome = doc is null ? "added" : "replaced";
        if (doc is null)
        {
            doc = new Document { Id = Guid.CreateVersion7(), Name = name, Collection = collection, ContentType = "text", Sha256 = "", CreatedAt = now };
            db.Documents.Add(doc);
        }
        else
        {
            // A previous PDF or auto-chunked version of the same name loses its pages along with its chunks.
            await db.Pages.Where(p => p.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
        }

        (doc.ContentType, doc.Language) = encoding is null ? TextExtractor.Classify(name) : ("binary", null);
        doc.Encoding = encoding;
        doc.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        doc.SizeBytes = bytes.Length;
        doc.ChunkSource = ChunkSources.Claude;
        doc.SourceDocumentId = sourceDocumentId;
        doc.PageCount = doc.OcrPages = doc.EmptyPages = doc.SuspectPages = doc.LayoutPages = 0;
        doc.OcrModel = null;
        doc.OcrAt = null;
        doc.Status = DocumentStatus.Pending;
        doc.Error = null;
        doc.UpdatedAt = now;

        await collections.EnsureExistsAsync(collection, ct);
        await files.SaveAsync(doc.Id, bytes, ct);
        await db.SaveChangesAsync(ct);

        await StoreAsync(doc, text, plan.Chunks, ct);
        return new ClaudeIndexResult(doc, outcome, plan.Chunks.Count, plan.Gaps);
    }

    /// <summary>Replaces an existing text document's chunks with new boundaries, without re-sending the file.</summary>
    public async Task<ClaudeIndexResult> SetChunksAsync(Document doc, IReadOnlyList<ChunkSpec> specs, CancellationToken ct)
    {
        if (doc.ContentType == "pdf")
            throw new ChunkPlanException(["A PDF's chunks follow its pages. Convert it to Markdown and add that with add_document instead."]);
        if (doc.Status is DocumentStatus.Ocr or DocumentStatus.Pending)
            throw new ChunkPlanException([$"\"{doc.Name}\" is still being processed; try again shortly."]);
        var text = doc.Text ?? ToText(await files.ReadAsync(doc.Id, ct)).Text;
        var plan = ChunkPlan.Validate(ChunkPlan.SplitLines(text), specs, Opt.Mcp.MaxChunkChars);
        if (!plan.Ok) throw new ChunkPlanException(plan.Errors);

        doc.ChunkSource = ChunkSources.Claude;
        await StoreAsync(doc, text, plan.Chunks, ct);
        return new ClaudeIndexResult(doc, "rechunked", plan.Chunks.Count, plan.Gaps);
    }

    /// <summary>
    /// Re-embeds a Claude-chunked document with its stored boundaries (used by re-index). Returns false if the stored
    /// chunks no longer fit the text, so the caller can fall back to the automatic chunker.
    /// </summary>
    public async Task<bool> RebuildAsync(Document doc, CancellationToken ct)
    {
        var specs = await db.Chunks.AsNoTracking().Where(c => c.DocumentId == doc.Id && c.StartLine != null && c.EndLine != null)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ChunkSpec(c.StartLine!.Value, c.EndLine!.Value, c.Title, c.HeadingPath, c.Summary, c.Kind, c.Symbol, c.Page, c.PageEnd, c.CreatedAt))
            .ToListAsync(ct);
        var text = ToText(await files.ReadAsync(doc.Id, ct)).Text;
        var plan = ChunkPlan.Validate(ChunkPlan.SplitLines(text), specs, Opt.Mcp.MaxChunkChars);
        if (!plan.Ok)
        {
            log.LogWarning("Stored chunks of {Name} no longer fit its text ({Error}); using the automatic chunker", doc.Name, plan.Errors[0]);
            return false;
        }
        await StoreAsync(doc, text, plan.Chunks, ct);
        return true;
    }

    async Task StoreAsync(Document doc, string text, List<PlannedChunk> chunks, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var vectors = await embedder.EmbedDocumentsAsync(chunks.Select(c => Chunker.EmbeddingText(
            doc.Name, doc.Language, c.Spec.Section, c.Spec.Page, c.Spec.Title, c.Spec.Symbol, c.Spec.Summary, c.Content,
            Opt.EmbeddingMaxChars)).ToList(), ct);
        if (vectors.Count > 0 && vectors[0].Length != RagDbContext.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"Embedding model returned {vectors[0].Length} dimensions; the database expects {RagDbContext.EmbeddingDimensions}.");

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Chunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
        db.Chunks.AddRange(chunks.Select((c, i) => new Chunk
        {
            DocumentId = doc.Id,
            Ordinal = i,
            Content = c.Content,
            HeadingPath = Trim(c.Spec.Section),
            Title = Trim(c.Spec.Title),
            Summary = Trim(c.Spec.Summary),
            Kind = c.Spec.Kind,
            Symbol = Trim(c.Spec.Symbol),
            StartLine = c.Spec.StartLine,
            EndLine = c.Spec.EndLine,
            Page = c.Spec.Page,
            PageEnd = c.Spec.PageEnd,
            // Re-indexing keeps each Claudomatic's original date.
            CreatedAt = c.Spec.CreatedAt ?? now,
            Embedding = new Vector(vectors[i]),
        }));
        doc.Text = text;
        doc.ChunkCount = chunks.Count;
        doc.Status = DocumentStatus.Ready;
        doc.Error = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Indexed {Collection}/{Name} with {Chunks} Claudomatics in {Seconds:F1}s",
            doc.Collection, doc.Name, chunks.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    /// <summary>The file as text, or as a hex dump (with its encoding) when it's binary.</summary>
    (string Text, string? Encoding) ToText(byte[] bytes)
    {
        if (TextExtractor.DecodeText(bytes) is { } text) return (text, null);
        if (bytes.Length > Opt.Mcp.MaxBinaryBytes)
            throw new ChunkPlanException([$"The file is binary and larger than the {Opt.Mcp.MaxBinaryBytes:N0}-byte limit for encoded files (Rag:Mcp:MaxBinaryBytes)."]);
        return (HexDump.Format(bytes), Encodings.Hex);
    }

    static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
