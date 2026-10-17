using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Ragamuffin.Layout;

/// <summary>
/// A word with its box in upright-image pixels (origin top-left). <see cref="Order"/> is its position in the PDF's
/// content stream (0 when unknown), which preserves the author's reading order inside dense tables.
/// </summary>
public sealed record WordBox(string Text, double X1, double Y1, double X2, double Y2, int Order = 0)
{
    public double CenterX => (X1 + X2) / 2;
    public double CenterY => (Y1 + Y2) / 2;
    public double Height => Math.Max(1, Y2 - Y1);
    public double Area => Math.Max(1, (X2 - X1) * (Y2 - Y1));
}

/// <summary>Turns layout regions (and the text found in them) into a page of Markdown.</summary>
public static partial class LayoutAssembler
{
    /// <summary>Page furniture: dropped from the indexed text.</summary>
    static readonly HashSet<string> Furniture = ["header", "footer", "number", "header_image", "footer_image", "seal"];

    static readonly HashSet<string> Figures = ["image", "chart"];

    /// <summary>
    /// Running headers, footers and page numbers are short. A "header" or "footer" region with more words than this is
    /// treated as content instead (e.g. a legend or note at the foot of the page), so it isn't silently dropped.
    /// </summary>
    public const int MaxFurnitureWords = 15;

    public static bool IsFurniture(string label) => Furniture.Contains(label);

    /// <summary>Whether a region is page furniture that should be dropped, judged by its label and its word count.</summary>
    public static bool IsFurniture(string label, string? text) =>
        Furniture.Contains(label) && WordCount(text) <= MaxFurnitureWords;

    static int WordCount(string? text) => (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static bool IsFigure(string label) => Figures.Contains(label);

    /// <summary>The glm-ocr task prompt suited to a region.</summary>
    public static string OcrPrompt(string label) => label switch
    {
        "table" => "Table Recognition:",
        "formula" => "Formula Recognition:",
        _ => "Text Recognition:",
    };

    /// <summary>Formats one region's text as Markdown, or null when there's nothing to keep.</summary>
    public static string? FormatRegion(string label, string? text)
    {
        // glm-ocr sometimes answers with an HTML table even for a region labelled as text; normalize to Markdown.
        var t = TablesToMarkdown((text ?? "").Trim());
        if (t.Length == 0) return null;
        return label switch
        {
            "doc_title" => "# " + OneLine(t),
            "paragraph_title" => "## " + OneLine(t),
            "figure_title" or "table_title" or "chart_title" => "*" + OneLine(t) + "*",
            "image" or "chart" => "[Figure text: " + string.Join(" / ", Lines(t)) + "]",
            _ => t,
        };
    }

    [GeneratedRegex(@"(?:<html>\s*<body>\s*)?<table\b.*?</table>(?:\s*</body>\s*</html>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTableBlockRegex();

    /// <summary>Replaces every HTML table in the text with its Markdown equivalent.</summary>
    public static string TablesToMarkdown(string text) =>
        text.Contains("<table", StringComparison.OrdinalIgnoreCase)
            ? HtmlTableBlockRegex().Replace(text, m => HtmlTableToMarkdown(m.Value) ?? StripTags(m.Value))
            : text;

    /// <summary>A region (and its table cells) with coordinates multiplied by <paramref name="factor"/>.</summary>
    public static LayoutRegion Scale(LayoutRegion region, double factor) => factor == 1 ? region : region with
    {
        Bbox = region.Bbox.Select(v => v * factor).ToArray(),
        Table = region.Table is { } t ? t with { Cells = t.Cells.Select(c => c.Select(v => v * factor).ToArray()).ToList() } : null,
    };

    public static string Join(IEnumerable<string?> parts) =>
        string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    static string OneLine(string t) => string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimStart('#', ' ');

    static IEnumerable<string> Lines(string t) => t.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    // ---------- tables ----------

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<(td|th)([^>]*)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColspanRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>Cells of an HTML table, row by row, with their colspan. Cell order matches the HTML.</summary>
    public static List<List<(string Text, int Span)>> ParseHtmlTable(string html) =>
        RowRegex().Matches(html).Select(row => CellRegex().Matches(row.Groups[1].Value)
            .Select(c => (Text: CellText(c.Groups[3].Value), Span: ColspanRegex().Match(c.Groups[2].Value) is { Success: true } m ? Math.Max(1, int.Parse(m.Groups[1].Value)) : 1))
            .ToList()).ToList();

    static string CellText(string inner) => StripTags(BreakRegex().Replace(inner, " "));

    static string StripTags(string html) =>
        string.Join(' ', WebUtility.HtmlDecode(TagRegex().Replace(html, " ")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string? HtmlTableToMarkdown(string html) => RowsToMarkdown(ParseHtmlTable(html));

    /// <summary>A Markdown pipe table; single-column tables become plain lines. Colspans repeat as empty cells.</summary>
    public static string? RowsToMarkdown(List<List<(string Text, int Span)>> rows)
    {
        var expanded = rows
            .Select(r => r.SelectMany(c => Enumerable.Repeat("", c.Span - 1).Prepend(c.Text)).ToList())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();
        if (expanded.Count == 0) return null;
        var cols = expanded.Max(r => r.Count);
        if (cols == 1) return string.Join('\n', expanded.Select(r => r[0]));

        var sb = new StringBuilder();
        for (var i = 0; i < expanded.Count; i++)
        {
            var cells = expanded[i].Concat(Enumerable.Repeat("", cols - expanded[i].Count)).Select(c => c.Replace("|", "\\|"));
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Rebuilds a table from the layout service's cell boxes (one per HTML cell, in HTML order) filled with the
    /// PDF's own words. Returns null when the boxes don't match the HTML or no words fall in any cell.
    /// </summary>
    public static string? TableFromCells(string html, IReadOnlyList<double[]> cells, IReadOnlyList<WordBox> words)
    {
        var rows = ParseHtmlTable(html);
        if (rows.Sum(r => r.Count) != cells.Count || cells.Count == 0) return null;

        var index = 0;
        var any = false;
        var coarse = false;
        var filled = rows.Select(r => r.Select(c =>
        {
            var box = cells[index++];
            var inCell = words.Where(w => w.CenterX >= box[0] - 2 && w.CenterX <= box[2] + 2 && w.CenterY >= box[1] - 2 && w.CenterY <= box[3] + 2).ToList();
            any |= inCell.Count > 0;
            var lines = WordsToLines(inCell);
            // A cell holding many lines means the detected grid is coarser than the real table.
            coarse |= lines.Count(ch => ch == '\n') >= MaxLinesPerCell;
            return (Text: lines.Replace('\n', ' '), c.Span);
        }).ToList()).ToList();
        return any && !coarse ? RowsToMarkdown(filled) : null;
    }

    /// <summary>Cells with more lines than this make a rebuilt table untrustworthy; callers fall back to content order.</summary>
    const int MaxLinesPerCell = 3;

    /// <summary>
    /// Words in the PDF's content-stream order, broken into lines where the text moves to a new line or jumps left.
    /// Used for tables whose cell grid couldn't be trusted: it keeps rows intact as the document's author wrote them.
    /// </summary>
    public static string ContentOrderLines(IEnumerable<WordBox> words)
    {
        var ordered = words.OrderBy(w => w.Order).ToList();
        if (ordered.Count == 0) return "";
        var heights = ordered.Select(w => w.Height).Order().ToList();
        var tolerance = heights[heights.Count / 2] * 0.5;

        var lines = new List<List<WordBox>> { new() { ordered[0] } };
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var w = ordered[i];
            var newLine = Math.Abs(w.CenterY - prev.CenterY) > tolerance || w.X1 < prev.X1 - tolerance;
            if (newLine) lines.Add([w]);
            else lines[^1].Add(w);
        }
        return string.Join('\n', lines.Select(l => string.Join(' ', l.Select(x => x.Text))));
    }

    // ---------- embedded words ----------

    /// <summary>
    /// Puts each word in the region it overlaps most (at least half the word). Words just outside a region's edge,
    /// which is common for the last word on a line, go to the nearest region within about two text heights. Anything
    /// farther is returned as unplaced so no text is lost when the layout model misses something.
    /// </summary>
    public static List<List<WordBox>> AssignWords(IReadOnlyList<double[]> regions, IEnumerable<WordBox> words, out List<WordBox> unplaced)
    {
        var assigned = regions.Select(_ => new List<WordBox>()).ToList();
        var leftover = new List<WordBox>();
        var all = words.ToList();
        foreach (var w in all)
        {
            var best = -1;
            var bestShare = 0.0;
            for (var i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                var overlap = Math.Max(0, Math.Min(w.X2, r[2]) - Math.Max(w.X1, r[0])) * Math.Max(0, Math.Min(w.Y2, r[3]) - Math.Max(w.Y1, r[1]));
                var share = overlap / w.Area;
                if (share > bestShare) { bestShare = share; best = i; }
            }
            if (best >= 0 && bestShare >= 0.5) assigned[best].Add(w);
            else leftover.Add(w);
        }

        unplaced = [];
        var heights = all.Select(w => w.Height).Order().ToList();
        var margin = heights.Count > 0 ? Math.Max(10, heights[heights.Count / 2] * 2) : 10;
        foreach (var w in leftover)
        {
            var nearest = -1;
            var nearestDistance = double.MaxValue;
            for (var i = 0; i < regions.Count; i++)
            {
                var d = DistanceToBox(w, regions[i]);
                if (d < nearestDistance) { nearestDistance = d; nearest = i; }
            }
            if (nearest >= 0 && nearestDistance <= margin) assigned[nearest].Add(w);
            else unplaced.Add(w);
        }
        return assigned;
    }

    /// <summary>Distance from a word's box to a region box (0 when they touch or overlap).</summary>
    static double DistanceToBox(WordBox w, double[] r)
    {
        var dx = Math.Max(0, Math.Max(r[0] - w.X2, w.X1 - r[2]));
        var dy = Math.Max(0, Math.Max(r[1] - w.Y2, w.Y1 - r[3]));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Groups words into lines (by vertical position) and orders each line left to right. Line breaks are kept.</summary>
    public static string WordsToLines(IEnumerable<WordBox> words)
    {
        var sorted = words.OrderBy(w => w.CenterY).ThenBy(w => w.X1).ToList();
        if (sorted.Count == 0) return "";
        var heights = sorted.Select(w => w.Height).Order().ToList();
        var tolerance = heights[heights.Count / 2] * 0.5;

        var lines = new List<List<WordBox>>();
        var lineCenter = double.NaN;
        foreach (var w in sorted)
        {
            if (lines.Count == 0 || Math.Abs(w.CenterY - lineCenter) > tolerance)
            {
                lines.Add([w]);
                lineCenter = w.CenterY;
            }
            else
            {
                lines[^1].Add(w);
                lineCenter = lines[^1].Average(x => x.CenterY);
            }
        }
        return string.Join('\n', lines.Select(l => string.Join(' ', l.OrderBy(x => x.X1).Select(x => x.Text))));
    }
}
