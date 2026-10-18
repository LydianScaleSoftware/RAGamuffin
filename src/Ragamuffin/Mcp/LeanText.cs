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
    /// <summary>"lines 10-40", "page 12", "pages 12-13", or "" when the location is unknown.</summary>
    public string Location => Page is { } p
        ? (PageEnd is { } e && e != p ? $"pages {p}-{e}" : $"page {p}")
        : StartLine is { } s ? $"lines {s}-{EndLine ?? s}" : "";

    /// <summary>The range argument that reads this passage with get_document, e.g. pages="12-13".</summary>
    public string ReadHint => Page is { } p
        ? $"pages=\"{p}-{PageEnd ?? p}\""
        : StartLine is { } s ? $"lines=\"{s}-{EndLine ?? s}\"" : "";
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
    /// Merges hits that are consecutive chunks of the same document (keeping the better rank), removing the overlap the
    /// chunker repeats between neighbours.
    /// </summary>
    public static List<Passage> Merge(IReadOnlyList<SearchHit> hits)
    {
        var used = new HashSet<long>();
        var byKey = hits.ToDictionary(h => (h.DocumentId, h.Ordinal));
        var passages = new List<Passage>();
        foreach (var hit in hits)
        {
            if (!used.Add(hit.ChunkId)) continue;
            // At most MaxRun chunks per passage, so one long stretch doesn't crowd out other results.
            var run = new List<SearchHit> { hit };
            for (var o = hit.Ordinal - 1; run.Count < MaxRun && byKey.TryGetValue((hit.DocumentId, o), out var prev) && used.Add(prev.ChunkId); o--) run.Insert(0, prev);
            for (var o = hit.Ordinal + 1; run.Count < MaxRun && byKey.TryGetValue((hit.DocumentId, o), out var next) && used.Add(next.ChunkId); o++) run.Add(next);

            var text = run[0].Content;
            foreach (var next in run.Skip(1)) text = JoinOverlapping(text, next.Content);
            passages.Add(new Passage(
                hit.DocumentName, hit.Collection, run.Select(r => r.HeadingPath).FirstOrDefault(h => h is not null),
                run[0].StartLine, run[^1].EndLine ?? run[^1].StartLine,
                run[0].Page, run[^1].Page, run.Any(r => r.Ocr), Dedent(text), []));
        }
        return passages;
    }

    const int MaxRun = 3;

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
    /// else the text appears.
    /// </summary>
    public static List<Passage> Dedupe(IReadOnlyList<Passage> passages, double threshold = 0.6)
    {
        var kept = new List<(Passage P, HashSet<string> Shingles)>();
        foreach (var p in passages)
        {
            var shingles = Shingles(p.Text);
            var dup = kept.FindIndex(k => Similarity(k.Shingles, shingles) >= threshold);
            if (dup >= 0)
            {
                var also = kept[dup].P.AlsoIn;
                if (!also.Contains(p.DocumentName) && p.DocumentName != kept[dup].P.DocumentName) also.Add(p.DocumentName);
                continue;
            }
            kept.Add((p, shingles));
        }
        return kept.Select(k => k.P).ToList();
    }

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
    /// The part of <paramref name="text"/> (at most <paramref name="maxChars"/>) with the most query terms, cut at line
    /// boundaries, marked with "…" where text was left out. Code lines stay intact.
    /// </summary>
    public static string Excerpt(string text, IReadOnlyList<string> terms, int maxChars)
    {
        text = text.Trim();
        if (text.Length <= maxChars) return text;

        var lines = text.Split('\n');
        // Very long lines (prose without breaks) are split into sentences so the window can still move.
        if (lines.Length < 4) lines = SentenceSplit(text);
        var scores = lines.Select(l => terms.Count == 0 ? 0 : terms.Count(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();

        int bestStart = 0, bestEnd = 0, bestScore = -1;
        for (var start = 0; start < lines.Length; start++)
        {
            var len = 0;
            var score = 0;
            var end = start;
            while (end < lines.Length && len + lines[end].Length + 1 <= maxChars)
            {
                len += lines[end].Length + 1;
                score += scores[end];
                end++;
            }
            if (end == start) continue;
            if (score > bestScore) { bestScore = score; bestStart = start; bestEnd = end; }
        }
        if (bestScore < 0)
            return text[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";

        var body = string.Join('\n', lines[bestStart..bestEnd]).Trim();
        return (bestStart > 0 ? "…" : "") + body + (bestEnd < lines.Length ? "…" : "");
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
                     $"To read more around a result, call get_document with its name and range{example}.\n";
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
        if (p.Heading is { } h) parts.Add("§" + (h.Length > 70 ? h[..69] + "…" : h));
        if (p.AlsoIn.Count > 0) parts.Add("also in: " + string.Join(", ", p.AlsoIn.Take(2)));
        return string.Join(" · ", parts);
    }
}
