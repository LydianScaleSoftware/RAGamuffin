using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;

namespace Ragamuffin.Search;

public sealed record SearchHit(
    Guid DocumentId,
    string DocumentName,
    string Collection,
    string ContentType,
    long ChunkId,
    int Ordinal,
    string? HeadingPath,
    int? StartLine,
    int? EndLine,
    int? Page,
    bool Ocr,
    string? OcrModel,
    double Score,
    double? Similarity,
    bool KeywordMatch,
    string Content)
{
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Kind { get; init; }
    public string? Symbol { get; init; }
    public int? PageEnd { get; init; }

    /// <summary>True when Claude chose this chunk's boundaries; such chunks are whole units and aren't merged.</summary>
    public bool ClaudeChunk { get; init; }

    /// <summary>How the document was encoded as text (e.g. "hex" for a binary file), or null for ordinary text.</summary>
    public string? Encoding { get; init; }
}

/// <summary>
/// Which collections a search covers. Named collections are always searched, muted or not; otherwise every collection
/// that isn't muted (unless <see cref="IncludeMuted"/>) and isn't excluded.
/// </summary>
public sealed record SearchScope(IReadOnlyList<string>? Collections = null, IReadOnlyList<string>? Exclude = null, bool IncludeMuted = false)
{
    public static readonly SearchScope Default = new();

    public static SearchScope For(string? collection) =>
        string.IsNullOrWhiteSpace(collection) ? Default : new([collection.Trim()]);
}

/// <summary>The collections a scope resolved to, and what it left out, for telling the agent.</summary>
public sealed record ResolvedScope(List<string> Searched, List<string> MutedSkipped, List<string> Unknown);

/// <summary>
/// Hybrid retrieval: pgvector cosine search and Postgres full-text search, merged with
/// reciprocal rank fusion so exact identifiers (class names, error codes) and paraphrased
/// questions both find their chunks.
/// </summary>
public sealed class SearchService(RagDbContext db, OllamaEmbedder embedder)
{
    const int RrfK = 60;

    public Task<List<SearchHit>> SearchAsync(string query, int topK, string? collection, CancellationToken ct) =>
        SearchAsync(query, topK, SearchScope.For(collection), ct);

    /// <summary>Resolves a scope against the collections that exist (names match case-insensitively).</summary>
    public async Task<ResolvedScope> ResolveAsync(SearchScope scope, CancellationToken ct)
    {
        var all = await db.Collections.AsNoTracking().Select(c => new { c.Name, c.Muted }).ToListAsync(ct);
        string? Find(string name) => all.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;
        var named = scope.Collections?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
        var excluded = (scope.Exclude ?? []).Select(Find).OfType<string>().ToHashSet();
        var unknown = named.Concat(scope.Exclude ?? []).Where(n => !string.IsNullOrWhiteSpace(n) && Find(n) is null).Distinct().ToList();
        if (named.Count > 0)
            return new(named.Select(Find).OfType<string>().Distinct().Where(n => !excluded.Contains(n)).ToList(), [], unknown);
        var searched = all.Where(c => (scope.IncludeMuted || !c.Muted) && !excluded.Contains(c.Name)).Select(c => c.Name).ToList();
        var skipped = scope.IncludeMuted ? [] : all.Where(c => c.Muted && !excluded.Contains(c.Name)).Select(c => c.Name).ToList();
        return new(searched, skipped, unknown);
    }

    public async Task<List<SearchHit>> SearchAsync(string query, int topK, SearchScope scope, CancellationToken ct) =>
        await SearchAsync(query, topK, (await ResolveAsync(scope, ct)).Searched, ct);

    async Task<List<SearchHit>> SearchAsync(string query, int topK, List<string> collections, CancellationToken ct)
    {
        if (collections.Count == 0) return [];
        topK = Math.Clamp(topK, 1, 50);
        var candidates = Math.Max(topK * 4, 20);
        var queryVector = new Vector(await embedder.EmbedQueryAsync(query, ct));

        // A document being OCR'd keeps serving its previous chunks until the run finishes. Pages of a PDF that a ready
        // conversion covers (by its chunks' page ranges) are left out, so the same text isn't found twice; a conversion
        // without page numbers covers the whole PDF.
        var chunks = db.Chunks.AsNoTracking()
            .Where(c => c.Document.Status == DocumentStatus.Ready || c.Document.Status == DocumentStatus.Ocr)
            .Where(c => !db.Documents.Any(o => o.SourceDocumentId == c.DocumentId && o.Status == DocumentStatus.Ready &&
                (!o.Chunks.Any(k => k.Page != null) ||
                 o.Chunks.Any(k => k.Page <= c.Page && (k.PageEnd ?? k.Page) >= c.Page))));
        chunks = chunks.Where(c => collections.Contains(c.Document.Collection));

        var semantic = await chunks
            .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
            .Select(c => new { c.Id, Distance = c.Embedding!.CosineDistance(queryVector) })
            .Take(candidates)
            .ToListAsync(ct);

        var keyword = await chunks
            .Where(c => c.Tsv.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .OrderByDescending(c => c.Tsv.RankCoverDensity(EF.Functions.WebSearchToTsQuery("english", query)))
            .Select(c => c.Id)
            .Take(candidates)
            .ToListAsync(ct);
        // Every word must match above, so a question with one word the passage lacks ("ORCA/C" in a toolbox manual's
        // Hello World listing) finds nothing; fill up with chunks that cover the most distinctive words.
        if (keyword.Count < candidates)
            keyword.AddRange((await CoverageAsync(query, collections.ToArray(), candidates, ct)).Where(id => !keyword.Contains(id)).Take(candidates - keyword.Count));

        var fused = Fuse(semantic.Select(s => s.Id).ToList(), keyword, topK);
        if (fused.Count == 0) return [];

        var ids = fused.Select(f => f.Id).ToList();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.DocumentId, c.Document.Name, c.Document.Collection, c.Document.ContentType,
                c.Ordinal, c.HeadingPath, c.StartLine, c.EndLine, c.Page, c.Content, c.Ocr, c.Document.OcrModel,
                c.Title, c.Summary, c.Kind, c.Symbol, c.PageEnd, c.Document.ChunkSource, c.Document.Encoding,
            })
            .ToDictionaryAsync(c => c.Id, ct);

        var distances = semantic.ToDictionary(s => s.Id, s => s.Distance);
        var keywordSet = keyword.ToHashSet();

        return fused.Where(f => rows.ContainsKey(f.Id)).Select(f =>
        {
            var r = rows[f.Id];
            return new SearchHit(r.DocumentId, r.Name, r.Collection, r.ContentType, r.Id, r.Ordinal,
                r.HeadingPath, r.StartLine, r.EndLine, r.Page, r.Ocr, r.Ocr ? r.OcrModel : null,
                Math.Round(f.Score, 5),
                distances.TryGetValue(f.Id, out var d) ? Math.Round(1 - d, 4) : null,
                keywordSet.Contains(f.Id),
                r.Content)
            {
                Title = r.Title, Summary = r.Summary, Kind = r.Kind, Symbol = r.Symbol, PageEnd = r.PageEnd,
                ClaudeChunk = r.ChunkSource == ChunkSources.Claude,
                Encoding = r.Encoding,
            };
        }).ToList();
    }

    /// <summary>
    /// Chunks matching any word of the query, best first by the summed rarity (inverse document frequency) of the
    /// distinct words they contain, so "hello" and "world" outweigh "program".
    /// </summary>
    async Task<List<long>> CoverageAsync(string query, string[] collections, int limit, CancellationToken ct) =>
        await db.Database.SqlQuery<long>($"""
            with terms as materialized (
                select quote_literal(l)::tsquery tq,
                       ln((select count(*) from chunks)::float / (1 + (select count(*) from chunks c where c.tsv @@ quote_literal(l)::tsquery))) idf
                from unnest(tsvector_to_array(to_tsvector('english', {query}))) l),
            any_term as materialized (select string_agg(tq::text, ' | ')::tsquery q from terms)
            select c.id as "Value"
            from chunks c join documents d on d.id = c.document_id, any_term a
            where a.q is not null and c.tsv @@ a.q
              and d.status in ({DocumentStatus.Ready}, {DocumentStatus.Ocr})
              and not exists (select 1 from documents o where o.source_document_id = d.id and o.status = {DocumentStatus.Ready}
                  and (not exists (select 1 from chunks k where k.document_id = o.id and k.page is not null)
                       or exists (select 1 from chunks k where k.document_id = o.id
                                  and c.page between k.page and coalesce(k.page_end, k.page))))
              and d.collection = any({collections})
            order by (select sum(t.idf) from terms t where c.tsv @@ t.tq) desc, ts_rank_cd(c.tsv, a.q) desc
            limit {limit}
            """).ToListAsync(ct);

    /// <summary>Reciprocal rank fusion of two ranked id lists.</summary>
    public static List<(long Id, double Score)> Fuse(IReadOnlyList<long> semantic, IReadOnlyList<long> keyword, int topK)
    {
        var scores = new Dictionary<long, double>();
        for (var i = 0; i < semantic.Count; i++)
            scores[semantic[i]] = scores.GetValueOrDefault(semantic[i]) + 1.0 / (RrfK + i + 1);
        for (var i = 0; i < keyword.Count; i++)
            scores[keyword[i]] = scores.GetValueOrDefault(keyword[i]) + 1.0 / (RrfK + i + 1);
        return scores.OrderByDescending(kv => kv.Value).Take(topK).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
