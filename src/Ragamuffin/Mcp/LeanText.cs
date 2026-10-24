using System.Text;
using System.Text.RegularExpressions;
using Ragamuffin.Search;

namespace Ragamuffin.Mcp;

/// <summary>A search result as shown to an agent; adjacent chunks of one document are merged into one passage.</summary>
public sealed record Passage(
    string DocumentName,
    string Collection,
    string? Heading,
    int? StartLine,
    int? EndLine,
    int? Page,
    int? PageEnd,
    bool Ocr,
    string Text,
    List<string> AlsoIn)
{
    /// <summary>The chunks this passage is made of, for get_chunk.</summary>
    public IReadOnlyList<long> ChunkIds { get; init; } = [];
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Kind { get; init; }
    public string? Symbol { get; init; }

    /// <summary>
    /// "lines 10-40", "page 12", "pages 12-13", or "" when the location is unknown. Lines win when a chunk has both:
    /// a Markdown conversion's chunk records the PDF pages it came from (see <see cref="SourcePages"/>).
    /// </summary>
    public string Location => StartLine is { } s
        ? $"lines {s}-{EndLine ?? s}"
        : Page is { } p ? (PageEnd is { } e && e != p ? $"pages {p}-{e}" : $"page {p}") : "";

    /// <summary>The PDF pages a converted chunk came from, e.g. "from pp. 112-113", or "" for other chunks.</summary>
    public string SourcePages => StartLine is not null && Page is { } p
        ? (PageEnd is { } e && e != p ? $"from pp. {p}-{e}" : $"from p. {p}")
        : "";

    /// <summary>The range argument that reads this passage with get_document, e.g. lines="40-60" or pages="12-13".</summary>
    public string ReadHint => StartLine is { } s
        ? $"lines=\"{s}-{EndLine ?? s}\""
        : Page is { } p ? $"pages=\"{p}-{PageEnd ?? p}\"" : "";
}

/// <summary>
/// Token-frugal formatting for small local models: merge neighbouring chunks, drop duplicate passages, keep the part
/// of each passage around the query terms, and fit everything in a budget, while always saying how to read more.
/// </summary>
public static partial class LeanText
{
    public const double CharsPerToken = 3.5;

    public static int Tokens(string text) => (int)Math.Ceiling(text.Length / CharsPerToken);

    public static int Chars(int tokens) => (int)(tokens * CharsPerToken);

    static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "with", "how", "what", "when", "where", "which", "why", "who", "does", "can", "use", "using",
        "into", "from", "that", "this", "are", "was", "you", "your", "about", "should", "would", "could", "need", "want",
        "get", "make", "write", "program", "show", "tell", "find", "there", "their", "have", "has", "not", "all", "any",
    ];

    [GeneratedRegex(@"[A-Za-z0-9_$#./:+-]{3,}")]
    private static partial Regex TermRegex();

    /// <summary>Distinctive terms of a query, lower-cased (identifiers like "QDStartUp" or "$A604" survive).</summary>
    public static List<string> QueryTerms(string query) => TermRegex().Matches(query)
        .Select(m => m.Value.Trim('.', ':', '-', '/').ToLowerInvariant())
        .Where(t => t.Length >= 3 && !StopWords.Contains(t))
        .Distinct()
        .ToList();

    /// <summary>
    /// Merges hits that are consecutive chunks of the same document, removing the overlap the chunker repeats between
    /// neighbours. A passage ranks by its best chunk's score plus the scores of neighbours that are nearly as relevant
    /// as the best result overall (by similarity): two neighbouring chunks that both match well (an explanation and the
    /// listing after it) are better evidence than either alone, but a run of weak matches mustn't outrank one strong,
    /// complete chunk.
    /// </summary>
    public static List<Passage> Merge(IReadOnlyList<SearchHit> hits)
    {
        var bestSimilarity = hits.Max(h => h.Similarity) ?? 0;
        double RunScore(List<SearchHit> run)
        {
            var best = run.MaxBy(r => r.Score)!;
            return best.Score + run.Where(r => r != best && (r.Similarity is null || r.Similarity >= bestSimilarity - SupportMargin)).Sum(r => r.Score);
        }

        var used = new HashSet<long>();
        var byKey = hits.ToDictionary(h => (h.DocumentId, h.Ordinal));
        var passages = new List<(Passage P, double Score)>();
        foreach (var hit in hits)
        {
            if (!used.Add(hit.ChunkId)) continue;
            // At most MaxRun chunks per passage, so one long stretch doesn't crowd out other results.
            var run = new List<SearchHit> { hit };
            // Chunks Claude chose are whole units already; only the server's fixed-size chunks are joined to neighbours.
            if (!hit.ClaudeChunk)
            {
                for (var o = hit.Ordinal - 1; run.Count < MaxRun && byKey.TryGetValue((hit.DocumentId, o), out var prev) && used.Add(prev.ChunkId); o--) run.Insert(0, prev);
                for (var o = hit.Ordinal + 1; run.Count < MaxRun && byKey.TryGetValue((hit.DocumentId, o), out var next) && used.Add(next.ChunkId); o++) run.Add(next);
            }

            var text = run[0].Content;
            foreach (var next in run.Skip(1)) text = JoinOverlapping(text, next.Content);
            passages.Add((new Passage(
                hit.DocumentName, hit.Collection, run.Select(r => r.HeadingPath).FirstOrDefault(h => h is not null),
                run[0].StartLine, run[^1].EndLine ?? run[^1].StartLine,
                run[0].Page, run[^1].PageEnd ?? run[^1].Page, run.Any(r => r.Ocr), Dedent(text), [])
            {
                ChunkIds = run.Select(r => r.ChunkId).ToList(),
                Title = hit.Title, Summary = hit.Summary, Kind = hit.Kind, Symbol = hit.Symbol,
            }, RunScore(run)));
        }
        return passages.OrderByDescending(p => p.Score).Select(p => p.P).ToList();
    }

    const int MaxRun = 3;

    /// <summary>How far below the best result's similarity a neighbour may be and still add to its passage's score.</summary>
    const double SupportMargin = 0.15;

    /// <summary>Removes indentation shared by every non-blank line (keeps code's relative indentation).</summary>
    public static string Dedent(string text)
    {
        var lines = text.Split('\n');
        var common = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart(' ').Length)
            .DefaultIfEmpty(0).Min();
        return common == 0 ? text : string.Join('\n', lines.Select(l => l.Length >= common ? l[common..] : l.TrimStart(' ')));
    }

    /// <summary>Appends <paramref name="next"/> to <paramref name="text"/>, dropping the start of next that repeats text's ending.</summary>
    public static string JoinOverlapping(string text, string next)
    {
        // The chunker repeats a few trailing lines of one chunk at the start of the next; find the longest such overlap.
        for (var k = Math.Min(Math.Min(text.Length, next.Length), 800); k >= 12; k--)
            if (text.AsSpan(text.Length - k).SequenceEqual(next.AsSpan(0, k)))
                return text + next[k..];
        return text + "\n" + next;
    }

    /// <summary>
    /// Drops passages that mostly repeat a better-ranked one (the same text in two editions of a manual), noting where
    /// else the text appears. When the repeat has different code (a C and a Pascal manual share whole lessons but not
    /// their listings), the one from the document whose name better fits <paramref name="query"/> is kept: "ORCA/C"
    /// picks "Toolbox C" over "Toolbox Pascal".
    /// </summary>
    public static List<Passage> Dedupe(IReadOnlyList<Passage> passages, string? query = null, double threshold = 0.6)
    {
        var kept = new List<(Passage P, HashSet<string> Shingles, HashSet<string> Code)>();
        foreach (var p in passages)
        {
            var shingles = Shingles(p.Text);
            var code = CodeLines(p.Text);
            var dup = kept.FindIndex(k => Similarity(k.Shingles, shingles) >= threshold);
            if (dup < 0)
            {
                kept.Add((p, shingles, code));
                continue;
            }
            var (keep, drop) = (kept[dup].P, p);
            var otherCode = kept[dup].Code.Count > 0 && code.Count > 0 && Similarity(kept[dup].Code, code) < 0.5;
            if (otherCode && query is not null && NameFit(p.DocumentName, query) > NameFit(keep.DocumentName, query))
            {
                (keep, drop) = (p with { AlsoIn = [.. kept[dup].P.AlsoIn] }, kept[dup].P);
                kept[dup] = (keep, shingles, code);
            }
            if (!keep.AlsoIn.Contains(drop.DocumentName) && drop.DocumentName != keep.DocumentName) keep.AlsoIn.Add(drop.DocumentName);
        }
        return kept.Select(k => k.P).ToList();
    }

    /// <summary>How many words of a document's name the query uses ("ORCA/C" and "GS-20 Toolbox C.txt" share "c").</summary>
    static int NameFit(string documentName, string query)
    {
        static IEnumerable<string> Words(string s) => WordRegex().Matches(s.ToLowerInvariant()).Select(m => m.Value);
        var asked = Words(query).Where(w => !StopWords.Contains(w)).ToHashSet();
        return Words(Path.GetFileNameWithoutExtension(documentName)).Distinct().Count(asked.Contains);
    }

    [GeneratedRegex(@"[a-z]+(?:-\d+)?")]
    private static partial Regex WordRegex();

    /// <summary>Lines that look like code (end in ; { or }, or start with #), without whitespace.</summary>
    static HashSet<string> CodeLines(string text) => text.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 1 && (l[^1] is ';' or '{' or '}' || l[0] == '#'))
        .Select(l => string.Concat(l.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant())
        .ToHashSet();

    static HashSet<string> Shingles(string text)
    {
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>();
        for (var i = 0; i + 4 < words.Length; i++) set.Add(string.Join(' ', words, i, 5));
        if (set.Count == 0 && words.Length > 0) set.Add(string.Join(' ', words));
        return set;
    }

    /// <summary>Share of the smaller passage's 5-word shingles found in the larger one.</summary>
    static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        return small.Count(large.Contains) / (double)small.Count;
    }

    /// <summary>
    /// The parts of <paramref name="text"/> (at most <paramref name="maxChars"/> in all) that cover the most query terms,
    /// cut at line boundaries and marked with "…" where text was left out. Rare terms count more than common ones, a
    /// term already covered counts less, and lines close together are kept together so code stays readable. Leftover
    /// room goes to the lines after each selected part.
    /// </summary>
    public static string Excerpt(string text, IReadOnlyList<string> terms, int maxChars)
    {
        text = text.Trim();
        if (text.Length <= maxChars) return text;

        var lines = text.Split('\n');
        // Very long lines (prose without breaks) are split into sentences so selection can still pick parts.
        if (lines.Length < 4) lines = SentenceSplit(text);
        var has = lines.Select(l => terms.Where(t => l.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList()).ToArray();
        // A line that keeps coming back to a term ("text" six times) is more likely about it than one mentioning it once.
        var tf = lines.Select(l => terms.ToDictionary(t => t, t => 1 + Math.Log(Math.Max(1, Occurrences(l, t))))).ToArray();
        if (terms.Count == 0 || has.All(h => h.Count == 0))
            return text[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";

        // Inverse document frequency over lines: a term in every line says little about where the answer is.
        var weight = terms.ToDictionary(t => t, t => Math.Log(1 + lines.Length / (1.0 + has.Count(h => h.Contains(t)))));
        var chosen = new bool[lines.Length];
        var covered = new HashSet<string>();
        int Cost(int i) => lines[i].Length + 1;
        var used = 0;

        // First cover every term once, preferring rare terms and, only mildly, shorter lines: the line that alone
        // mentions "text" matters more than a short line repeating a common word.
        while (true)
        {
            var best = -1;
            var bestGain = 0.0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (chosen[i] || used + Cost(i) + 2 > maxChars) continue;
                var gain = has[i].Where(t => !covered.Contains(t)).Sum(t => weight[t] * tf[i][t]) / Math.Pow(Cost(i) + 40, 0.25);
                if (gain > bestGain) { bestGain = gain; best = i; }
            }
            if (best < 0) break;
            chosen[best] = true;
            used += Cost(best) + 2;
            covered.UnionWith(has[best]);
        }

        // Then the next most relevant lines (a second paragraph on the same point, a function's prototype), within three
        // quarters of the room left; the rest goes to context after them.
        var repeatLimit = used + (maxChars - used) * 3 / 4;
        while (true)
        {
            var best = -1;
            var bestGain = 0.0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (chosen[i] || has[i].Count == 0 || used + Cost(i) + 2 > repeatLimit) continue;
                var gain = has[i].Sum(t => weight[t] * tf[i][t]) / Math.Pow(Cost(i) + 40, 0.25);
                if (gain > bestGain) { bestGain = gain; best = i; }
            }
            if (best < 0) break;
            chosen[best] = true;
            used += Cost(best) + 2;
        }

        // Then join chosen lines that are close together, so a few related lines read as one passage.
        for (var i = 0; i < lines.Length; i++)
        {
            if (!chosen[i]) continue;
            var j = i + 1;
            while (j < lines.Length && !chosen[j] && j - i <= 4) j++;
            if (j >= lines.Length || !chosen[j] || j == i + 1) continue;
            var extra = Enumerable.Range(i + 1, j - i - 1).Sum(Cost) - 2;
            if (used + extra > maxChars) continue;
            for (var k = i + 1; k < j; k++) chosen[k] = true;
            used += extra;
        }

        // Spend what's left on the lines following each selected part (an explanation's example, a listing's body).
        for (var grew = true; grew;)
        {
            grew = false;
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (!chosen[i] || chosen[i + 1] || used + Cost(i + 1) > maxChars) continue;
                chosen[i + 1] = true;
                used += Cost(i + 1);
                grew = true;
                i++;
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!chosen[i]) continue;
            if (i == 0 || !chosen[i - 1]) sb.Append(sb.Length == 0 ? (i > 0 ? "…" : "") : "\n…\n");
            else sb.Append('\n');
            sb.Append(lines[i]);
        }
        if (!chosen[^1]) sb.Append('…');
        return sb.ToString();
    }

    static int Occurrences(string line, string term)
    {
        var n = 0;
        for (var i = line.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = line.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    static string[] SentenceSplit(string text) =>
        Regex.Split(text, @"(?<=[.!?;:])\s+(?=[A-Z0-9(\[""'])").Where(s => s.Length > 0).ToArray();

    /// <summary>Character allowance per result rank: the best results get more room.</summary>
    static readonly int[] RankWeights = [10, 7, 5, 4, 4, 3, 3, 3, 3, 3];

    /// <summary>The search answer for an agent: compact citations and excerpts within the token budget.</summary>
    public static string FormatSearch(string query, IReadOnlyList<Passage> passages, int budgetTokens)
    {
        if (passages.Count == 0)
            return $"No matching passages for \"{query}\". Try other words, an exact identifier, or no collection filter.";

        var example = passages.FirstOrDefault(p => p.ReadHint.Length > 0) is { } first
            ? $" e.g. get_document(\"{first.DocumentName}\", {first.ReadHint})"
            : "";
        var header = $"{passages.Count} result{(passages.Count == 1 ? "" : "s")} for \"{query}\" (best first). " +
                     $"Long chunks may be shortened here; get_chunk(id) returns a chunk whole. To read around a result, " +
                     $"call get_document with its name and range{example}.\n";
        var terms = QueryTerms(query);
        var citations = passages.Select(Citation).ToList();
        var fixedChars = header.Length + citations.Sum(c => c.Length + 2);
        var available = Math.Max(200, Chars(budgetTokens) - fixedChars);
        var weights = passages.Select((_, i) => RankWeights[Math.Min(i, RankWeights.Length - 1)]).ToArray();
        var unit = available / (double)weights.Sum();

        var sb = new StringBuilder(header);
        for (var i = 0; i < passages.Count; i++)
        {
            var allowance = Math.Max(120, (int)(weights[i] * unit));
            sb.Append('\n').Append($"[{i + 1}] ").Append(citations[i]).Append('\n');
            sb.Append(Excerpt(passages[i].Text, terms, allowance)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>"ORCA_C_2.2_Manual.txt · orca/goldengate · lines 1143-1165 · §Graphics · also in: GS-06 ORCA:C 2.0.txt"</summary>
    public static string Citation(Passage p)
    {
        var parts = new List<string> { p.DocumentName, p.Collection };
        if (p.Location.Length > 0) parts.Add(p.Location + (p.Ocr ? " (OCR)" : ""));
        if (p.SourcePages.Length > 0) parts.Add(p.SourcePages);
        if (p.Heading is { } h) parts.Add("§" + (h.Length > 120 ? h[..119] + "…" : h));
        if (p.Title is { } t) parts.Add($"\"{t}\"");
        if (p.Kind is { } k) parts.Add(p.Symbol is { } sym ? $"{k} {sym}" : k);
        if (p.ChunkIds.Count > 0) parts.Add("chunk " + string.Join("+", p.ChunkIds));
        if (p.AlsoIn.Count > 0) parts.Add("also in: " + string.Join(", ", p.AlsoIn.Take(2)));
        return string.Join(" · ", parts);
    }
}
