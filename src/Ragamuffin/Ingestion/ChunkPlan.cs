using System.Text.RegularExpressions;

namespace Ragamuffin.Ingestion;

/// <summary>One chunk Claude chose: a range of whole lines (1-based, inclusive) plus what it is about.</summary>
public sealed record ChunkSpec(
    int StartLine,
    int EndLine,
    string? Title = null,
    string? Section = null,
    string? Summary = null,
    string? Kind = null,
    string? Symbol = null,
    int? Page = null,
    int? PageEnd = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>A validated chunk with its text cut from the document.</summary>
public sealed record PlannedChunk(ChunkSpec Spec, string Content);

/// <summary>Lines no chunk covers (only reported when they hold text), so Claude can tell a deliberate skip from a slip.</summary>
public sealed record LineGap(int StartLine, int EndLine);

public sealed record ChunkPlanResult(List<PlannedChunk> Chunks, List<string> Errors, List<LineGap> Gaps)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Checks Claude's chunk boundaries against a document's lines: every range inside the document, in order, not
/// overlapping, not empty and not over the size limit. Chunks may be any size up to that limit: a table, a listing
/// with its explanation, or a whole function stays one chunk.
/// </summary>
public static partial class ChunkPlan
{
    public const int MaxTitleLength = 300;
    public const int MaxKindLength = 40;

    /// <summary>Kinds the skills use; others are accepted as long as they're short lowercase words.</summary>
    public static readonly string[] SuggestedKinds = ["prose", "section", "table", "example", "listing", "code", "function", "class", "reference", "glossary", "index"];

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"^<!--.*-->$")]
    private static partial Regex CommentLine();

    public static string[] SplitLines(string text) => text.Split('\n');

    public static ChunkPlanResult Validate(string[] lines, IReadOnlyList<ChunkSpec> specs, int maxChunkChars)
    {
        var errors = new List<string>();
        var chunks = new List<PlannedChunk>();
        if (specs.Count == 0) errors.Add("No chunks given.");

        var previousEnd = 0;
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            var label = $"Chunk {i + 1} (lines {s.StartLine}-{s.EndLine})";
            var before = errors.Count;
            if (s.StartLine < 1) errors.Add($"{label}: start_line must be at least 1.");
            if (s.EndLine < s.StartLine) errors.Add($"{label}: end_line is before start_line.");
            if (s.EndLine > lines.Length) errors.Add($"{label}: end_line is past the end of the document ({lines.Length} lines).");
            if (s.StartLine <= previousEnd) errors.Add($"{label}: overlaps or comes before the previous chunk, which ends at line {previousEnd}. Give chunks in order without overlap.");
            if (s.Title is { Length: > MaxTitleLength }) errors.Add($"{label}: title is longer than {MaxTitleLength} characters.");
            if (s.Symbol is { Length: > MaxTitleLength }) errors.Add($"{label}: symbol is longer than {MaxTitleLength} characters.");
            if (s.Kind is { } kind && (kind.Length > MaxKindLength || !KindPattern().IsMatch(kind)))
                errors.Add($"{label}: kind must be a short lowercase word such as {string.Join(", ", SuggestedKinds.Take(6))}.");
            if (s.Page is < 1 || (s.PageEnd is { } pe && (s.Page is null || pe < s.Page)))
                errors.Add($"{label}: pages must be positive, with the last page not before the first.");
            if (errors.Count > before) { previousEnd = Math.Max(previousEnd, s.EndLine); continue; }

            var content = string.Join('\n', lines[(s.StartLine - 1)..s.EndLine]).TrimEnd();
            if (content.Trim().Length == 0) errors.Add($"{label}: the lines are blank.");
            else if (content.Length > maxChunkChars) errors.Add($"{label}: {content.Length:N0} characters is over the {maxChunkChars:N0}-character limit; split it.");
            else chunks.Add(new PlannedChunk(s, content));
            previousEnd = s.EndLine;
        }

        return new ChunkPlanResult(chunks, errors, errors.Count == 0 ? FindGaps(lines, specs) : []);
    }

    /// <summary>
    /// Uncovered stretches of lines that contain text. Blank lines and comment-only lines (the skills' page markers,
    /// <c>&lt;!-- page 112 --&gt;</c>) between chunks are expected and don't count.
    /// </summary>
    public static List<LineGap> FindGaps(string[] lines, IReadOnlyList<ChunkSpec> specs)
    {
        bool HasText(int n) => lines[n - 1].Trim() is var t && t.Length > 0 && !CommentLine().IsMatch(t);
        var gaps = new List<LineGap>();
        var next = 1;
        foreach (var s in specs.Append(new ChunkSpec(lines.Length + 1, lines.Length + 1)))
        {
            if (s.StartLine > next)
            {
                var first = Enumerable.Range(next, s.StartLine - next).FirstOrDefault(HasText);
                if (first > 0)
                {
                    var last = Enumerable.Range(next, s.StartLine - next).Last(HasText);
                    gaps.Add(new LineGap(first, last));
                }
            }
            next = Math.Max(next, s.EndLine + 1);
        }
        return gaps;
    }
}
