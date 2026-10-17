using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;

namespace Ragamuffin.Stats;

/// <summary>Where a document's indexed text comes from.</summary>
public enum TextOrigin
{
    /// <summary>Markdown, plain text or source code: the file's own text.</summary>
    OriginalText,
    /// <summary>PDF using its embedded text as stored.</summary>
    PdfEmbedded,
    /// <summary>PDF using its embedded text, arranged by layout analysis on at least one page.</summary>
    PdfEmbeddedLayout,
    /// <summary>PDF whose text all comes from local OCR.</summary>
    PdfOcr,
    /// <summary>PDF with some pages from embedded text and some from OCR.</summary>
    PdfMixed,
    /// <summary>PDF with no usable text yet (needs OCR, or not analyzed).</summary>
    PdfNoText,
}

/// <summary>Per-document page counts by text source, for a PDF.</summary>
public sealed record PageMix(int Embedded, int Layout, int OcrWithLayout, int OcrWholePage, int Blank)
{
    public int Ocr => OcrWithLayout + OcrWholePage;
    public int Total => Embedded + Layout + Ocr + Blank;

    public static PageMix operator +(PageMix a, PageMix b) =>
        new(a.Embedded + b.Embedded, a.Layout + b.Layout, a.OcrWithLayout + b.OcrWithLayout, a.OcrWholePage + b.OcrWholePage, a.Blank + b.Blank);

    public static readonly PageMix Empty = new(0, 0, 0, 0, 0);
}

public sealed record StatsGroup(
    string Name,
    bool IsDefault,
    int Documents,
    int Pages,
    int Chunks,
    long SizeBytes,
    long Characters,
    Dictionary<string, int> Types,
    Dictionary<string, int> Languages,
    Dictionary<string, int> Origins,
    PageMix PdfPages,
    Dictionary<string, int> Statuses,
    int SuspectPages,
    int DocumentsWithNotices,
    int AddedLast7Days,
    DateTimeOffset? LastUpdated)
{
    public double AvgChunksPerDocument => Documents == 0 ? 0 : Math.Round(Chunks / (double)Documents, 1);
    public int AvgChunkChars => Chunks == 0 ? 0 : (int)(Characters / Chunks);

    /// <summary>PDF pages with no usable text: what an OCR run would cover.</summary>
    public int PagesNeedingOcr => PdfPages.Blank;

    /// <summary>PDF pages using raw embedded text: what a layout run would cover.</summary>
    public int PagesWithoutLayout => PdfPages.Embedded;
}

public sealed record LargestDocument(string Name, string Collection, string ContentType, int Pages, int Chunks, long SizeBytes);

public sealed record StatsReport(
    StatsGroup Total,
    List<StatsGroup> Collections,
    List<LargestDocument> Largest,
    Dictionary<string, int> OcrModels,
    long DatabaseBytes,
    string EmbeddingModel,
    DateTimeOffset GeneratedAt);

public sealed class StatsService(RagDbContext db, Microsoft.Extensions.Options.IOptions<RagOptions> options)
{
    sealed record DocRow(Guid Id, string Name, string Collection, string ContentType, string? Language, string Status, string? Error,
        int PageCount, int EmptyPages, int SuspectPages, int ChunkCount, long SizeBytes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public async Task<StatsReport> BuildAsync(CancellationToken ct)
    {
        var docs = await db.Documents.AsNoTracking()
            .Select(d => new DocRow(d.Id, d.Name, d.Collection, d.ContentType, d.Language, d.Status, d.Error,
                d.PageCount, d.EmptyPages, d.SuspectPages, d.ChunkCount, d.SizeBytes, d.CreatedAt, d.UpdatedAt))
            .ToListAsync(ct);
        var characters = await db.Chunks.AsNoTracking()
            .GroupBy(c => c.DocumentId)
            .Select(g => new { Id = g.Key, Chars = g.Sum(c => (long)c.Content.Length) })
            .ToDictionaryAsync(x => x.Id, x => x.Chars, ct);

        var mixes = await PageMixesAsync(db, ct);

        var collections = await db.Collections.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
        var groups = collections
            .Union(docs.Select(d => d.Collection))
            .Select(name => Summarize(name, name == Collection.Default, docs.Where(d => d.Collection == name).ToList(), mixes, characters))
            .OrderByDescending(g => g.IsDefault)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var largest = docs
            .OrderByDescending(d => d.ChunkCount).ThenByDescending(d => d.PageCount)
            .Take(5)
            .Select(d => new LargestDocument(d.Name, d.Collection, d.ContentType, d.PageCount, d.ChunkCount, d.SizeBytes))
            .ToList();
        var ocrModels = await db.Pages.AsNoTracking()
            .Where(p => p.Source == PageSource.Ocr && p.OcrModel != null)
            .GroupBy(p => p.OcrModel!)
            .Select(g => new { Model = g.Key, Pages = g.Count() })
            .ToDictionaryAsync(x => x.Model, x => x.Pages, ct);
        var databaseBytes = await db.Database
            .SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
            .SingleAsync(ct);

        return new(Summarize("All collections", false, docs, mixes, characters), groups, largest, ocrModels,
            databaseBytes, options.Value.EmbeddingModel, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Page counts per PDF by text source. Pages without usable text keep the "embedded" source, so they're moved to
    /// the blank bucket using each document's empty-page count. "Layout" on an OCR page means it was read region by region.
    /// </summary>
    public static async Task<Dictionary<Guid, PageMix>> PageMixesAsync(RagDbContext db, CancellationToken ct)
    {
        var empty = await db.Documents.AsNoTracking().Where(d => d.ContentType == "pdf")
            .ToDictionaryAsync(d => d.Id, d => d.EmptyPages, ct);
        var groups = await db.Pages.AsNoTracking()
            .GroupBy(p => new { p.DocumentId, p.Source, HasLayout = p.LayoutJson != null })
            .Select(g => new { g.Key.DocumentId, g.Key.Source, g.Key.HasLayout, Count = g.Count() })
            .ToListAsync(ct);
        return groups.GroupBy(g => g.DocumentId).ToDictionary(g => g.Key, g =>
        {
            int Count(string source, bool? layout = null) =>
                g.Where(x => x.Source == source && (layout is null || x.HasLayout == layout)).Sum(x => x.Count);
            var blank = empty.GetValueOrDefault(g.Key);
            return new PageMix(
                Embedded: Math.Max(0, Count(PageSource.Embedded) - blank),
                Layout: Count(PageSource.Layout),
                OcrWithLayout: Count(PageSource.Ocr, layout: true),
                OcrWholePage: Count(PageSource.Ocr, layout: false),
                Blank: blank);
        });
    }

    static StatsGroup Summarize(string name, bool isDefault, List<DocRow> docs, Dictionary<Guid, PageMix> mixes, Dictionary<Guid, long> characters)
    {
        var pdfMix = docs.Where(d => d.ContentType == "pdf")
            .Select(d => mixes.GetValueOrDefault(d.Id) ?? UnanalyzedPdf(d))
            .Aggregate(PageMix.Empty, (a, b) => a + b);

        return new StatsGroup(
            name,
            isDefault,
            Documents: docs.Count,
            Pages: docs.Where(d => d.ContentType == "pdf").Sum(d => Math.Max(d.PageCount, (mixes.GetValueOrDefault(d.Id) ?? PageMix.Empty).Total)),
            Chunks: docs.Sum(d => d.ChunkCount),
            SizeBytes: docs.Sum(d => d.SizeBytes),
            Characters: docs.Sum(d => characters.GetValueOrDefault(d.Id)),
            Types: Count(docs.Select(d => d.ContentType)),
            Languages: Count(docs.Where(d => d.ContentType == "code").Select(d => d.Language ?? "other")),
            Origins: Count(docs.Select(d => Classify(d.ContentType, mixes.GetValueOrDefault(d.Id)).ToString())),
            PdfPages: pdfMix,
            Statuses: Count(docs.Select(d => d.Status)),
            SuspectPages: docs.Sum(d => d.SuspectPages),
            DocumentsWithNotices: docs.Count(d => d.Status != DocumentStatus.Failed && d.Status != DocumentStatus.NeedsOcr && d.Error != null),
            AddedLast7Days: docs.Count(d => d.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-7)),
            LastUpdated: docs.Count == 0 ? null : docs.Max(d => d.UpdatedAt));
    }

    /// <summary>A PDF with no page rows yet (e.g. failed before page analysis): count its pages as blank.</summary>
    static PageMix UnanalyzedPdf(DocRow d) => PageMix.Empty with { Blank = d.PageCount };

    static Dictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Classifies where a document's text comes from, given its PDF page mix (null for non-PDFs or unanalyzed PDFs).</summary>
    public static TextOrigin Classify(string contentType, PageMix? pages)
    {
        if (contentType != "pdf") return TextOrigin.OriginalText;
        if (pages is null) return TextOrigin.PdfNoText;
        var own = pages.Embedded + pages.Layout;
        return (own, pages.Ocr) switch
        {
            (0, 0) => TextOrigin.PdfNoText,
            (0, _) => TextOrigin.PdfOcr,
            (_, > 0) => TextOrigin.PdfMixed,
            _ => pages.Layout > 0 ? TextOrigin.PdfEmbeddedLayout : TextOrigin.PdfEmbedded,
        };
    }
}
