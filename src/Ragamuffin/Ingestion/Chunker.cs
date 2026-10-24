using System.Text;
using System.Text.RegularExpressions;

namespace Ragamuffin.Ingestion;

public sealed record ChunkDraft(string Content, string? HeadingPath, int? StartLine, int? EndLine, int? Page);

public sealed record ChunkerSettings(int TargetChars, int MaxChars, int OverlapChars);

/// <summary>
/// Type-aware chunker. Text is first cut into blocks (markdown sections/paragraphs, code groups
/// separated by blank lines, PDF paragraphs per page), then blocks are packed into chunks of
/// roughly <see cref="ChunkerSettings.TargetChars"/>, never exceeding MaxChars, carrying a few
/// trailing lines forward as overlap.
/// </summary>
public sealed partial class Chunker(ChunkerSettings settings)
{
    readonly record struct Line(int Number, string Text);

    sealed record Block(List<Line> Lines, string? HeadingPath, int? Page)
    {
        public int Length => Lines.Sum(l => l.Text.Length + 1);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    public List<ChunkDraft> Chunk(ExtractedDocument doc)
    {
        return doc.ContentType switch
        {
            "markdown" => Pack(MarkdownBlocks(doc.Text), keepLines: true, splitOnHeading: true),
            "code" => Pack(ParagraphBlocks(ToLines(doc.Text), null, null), keepLines: true, splitOnHeading: false),
            "pdf" => ChunkPdf(doc.Pages!),
            _ => Pack(ParagraphBlocks(ToLines(doc.Text), null, null), keepLines: true, splitOnHeading: false),
        };
    }

    static List<Line> ToLines(string text) =>
        text.Split('\n').Select((t, i) => new Line(i + 1, t.TrimEnd())).ToList();

    /// <summary>Groups lines into blocks separated by blank lines.</summary>
    static List<Block> ParagraphBlocks(List<Line> lines, string? heading, int? page)
    {
        var blocks = new List<Block>();
        var current = new List<Line>();
        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
            {
                if (current.Count > 0) { blocks.Add(new Block(current, heading, page)); current = []; }
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) blocks.Add(new Block(current, heading, page));
        return blocks;
    }

    /// <summary>
    /// PDF pages are chunked one page at a time (chunks never span pages, so page citations stay exact). Pages from
    /// layout analysis or OCR carry Markdown headings, and the heading breadcrumb carries over from page to page.
    /// </summary>
    List<ChunkDraft> ChunkPdf(IReadOnlyList<PageText> pages)
    {
        var headings = new string?[6];
        var chunks = new List<ChunkDraft>();
        foreach (var page in pages)
        {
            var blocks = page.Markdown
                ? MarkdownBlocks(page.Text, headings, page.Page)
                : ParagraphBlocks(ToLines(page.Text), Breadcrumb(headings), page.Page);
            chunks.AddRange(Pack(blocks, keepLines: false, splitOnHeading: page.Markdown));
        }
        return chunks;
    }

    static string? Breadcrumb(string?[] headings) =>
        headings.Any(h => h != null) ? string.Join(" > ", headings.Where(h => h != null)) : null;

    /// <summary>
    /// Paragraph blocks tagged with their heading breadcrumb; fenced code is never split at blank lines.
    /// <paramref name="headings"/> carries the heading stack in and out, so a breadcrumb can continue across pages.
    /// </summary>
    static List<Block> MarkdownBlocks(string text, string?[]? headings = null, int? page = null)
    {
        var blocks = new List<Block>();
        headings ??= new string?[6];
        var path = Breadcrumb(headings);
        var current = new List<Line>();
        var inFence = false;

        void Flush()
        {
            if (current.Count > 0) blocks.Add(new Block(current, path, page));
            current = [];
        }

        foreach (var line in ToLines(text))
        {
            var trimmed = line.Text.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                current.Add(line);
                continue;
            }
            if (inFence) { current.Add(line); continue; }

            var m = HeadingRegex().Match(line.Text);
            if (m.Success)
            {
                Flush();
                var level = m.Groups[1].Length;
                headings[level - 1] = m.Groups[2].Value;
                for (var i = level; i < headings.Length; i++) headings[i] = null;
                path = Breadcrumb(headings);
                current.Add(line);
                continue;
            }

            if (line.Text.Length == 0)
            {
                // Keep a heading attached to its first paragraph.
                if (current.Count > 0 && !(current.Count == 1 && HeadingRegex().IsMatch(current[0].Text)))
                    Flush();
                continue;
            }
            current.Add(line);
        }
        Flush();
        return blocks;
    }

    List<ChunkDraft> Pack(List<Block> blocks, bool keepLines, bool splitOnHeading)
    {
        var chunks = new List<ChunkDraft>();
        var lines = new List<Line>();
        var len = 0;
        string? heading = null;
        int? page = null;
        var fresh = false; // true once the current chunk holds more than carried-over overlap

        void Clear() { lines = []; len = 0; fresh = false; }

        void Emit(bool carryOverlap)
        {
            if (fresh)
            {
                chunks.Add(new ChunkDraft(
                    string.Join('\n', lines.Select(l => l.Text)),
                    heading,
                    keepLines ? lines[0].Number : null,
                    keepLines ? lines[^1].Number : null,
                    page));
            }
            if (!carryOverlap) { Clear(); return; }

            var carry = new List<Line>();
            var carryLen = 0;
            for (var i = lines.Count - 1; i > 0; i--)
            {
                var l = lines[i].Text.Length + 1;
                if (carryLen + l > settings.OverlapChars) break;
                carry.Insert(0, lines[i]);
                carryLen += l;
            }
            while (carry.Count > 0 && carry[0].Text.Length == 0) { carryLen -= 1; carry.RemoveAt(0); }
            lines = carry;
            len = carryLen;
            fresh = false;
        }

        foreach (var block in blocks)
        {
            if (block.Page != page)
                Emit(carryOverlap: false);
            else if (splitOnHeading && block.HeadingPath != heading)
            {
                // New section: start a fresh chunk unless the current one is too small to stand alone
                // (e.g. just a heading) and the two fit together.
                if (!fresh || TooBig(len, block.Length))
                    Emit(carryOverlap: false);
                else
                    heading = MergeHeadings(heading, block.HeadingPath);
            }

            foreach (var piece in SplitOversized(block.Lines))
            {
                var pieceLen = piece.Sum(l => l.Text.Length + 1);
                if (fresh && TooBig(len, 1 + pieceLen))
                    Emit(carryOverlap: true);
                if (len + 1 + pieceLen > settings.MaxChars)
                    Clear();
                if (!fresh)
                {
                    heading = block.HeadingPath;
                    page = block.Page;
                }
                if (lines.Count > 0) { lines.Add(new Line(lines[^1].Number, "")); len += 1; }
                lines.AddRange(piece);
                len += pieceLen;
                fresh = true;
            }
        }
        Emit(carryOverlap: false);
        return chunks;
    }

    /// <summary>
    /// Whether adding <paramref name="add"/> chars to a chunk of <paramref name="len"/> chars should start a new chunk.
    /// Chunks aim for TargetChars, but a tiny chunk (e.g. a lone heading) may grow up to MaxChars rather than stand alone.
    /// </summary>
    bool TooBig(int len, int add) =>
        len + add > settings.MaxChars || (len + add > settings.TargetChars && len >= settings.TargetChars / 4);

    /// <summary>Splits a block into roughly TargetChars pieces, breaking over-long lines at whitespace.</summary>
    IEnumerable<List<Line>> SplitOversized(List<Line> block)
    {
        var piece = new List<Line>();
        var len = 0;
        foreach (var line in block)
        {
            foreach (var part in SplitLine(line))
            {
                var l = part.Text.Length + 1;
                if (piece.Count > 0 && TooBig(len, l))
                {
                    yield return piece;
                    piece = [];
                    len = 0;
                }
                piece.Add(part);
                len += l;
            }
        }
        if (piece.Count > 0) yield return piece;
    }

    IEnumerable<Line> SplitLine(Line line)
    {
        var text = line.Text;
        var limit = settings.TargetChars;
        while (text.Length > limit)
        {
            var cut = text.LastIndexOf(' ', limit);
            if (cut < limit / 2) cut = limit;
            yield return new Line(line.Number, text[..cut]);
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0 || line.Text.Length == 0) yield return new Line(line.Number, text);
    }

    /// <summary>Breadcrumb for a chunk spanning two sections: the deeper one if nested, else their common ancestor.</summary>
    static string? MergeHeadings(string? current, string? next)
    {
        if (current is null || next is null) return next ?? current;
        if (next.StartsWith(current + " > ")) return next;
        var a = current.Split(" > ");
        var b = next.Split(" > ");
        var common = a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToList();
        return common.Count == 0 ? null : string.Join(" > ", common);
    }

    /// <summary>Text sent to the embedding model: a short context header plus the chunk.</summary>
    public static string EmbeddingText(string documentName, ExtractedDocument doc, ChunkDraft chunk) =>
        EmbeddingText(documentName, doc.Language, chunk.HeadingPath, chunk.Page, null, null, null, chunk.Content, int.MaxValue);

    /// <summary>
    /// Text sent to the embedding model: document, section, page, title and symbol on the first line, then the summary,
    /// then the content cut to <paramref name="maxChars"/> in all. The model only reads its first 2,048 tokens, so what
    /// describes the chunk comes first.
    /// </summary>
    public static string EmbeddingText(string documentName, string? language, string? headingPath, int? page,
        string? title, string? symbol, string? summary, string content, int maxChars)
    {
        var sb = new StringBuilder(documentName);
        if (language != null) sb.Append(" (").Append(language).Append(')');
        if (headingPath != null) sb.Append(" > ").Append(headingPath);
        if (page != null) sb.Append(" p.").Append(page);
        if (title != null) sb.Append(" | ").Append(title);
        if (symbol != null && symbol != title) sb.Append(" | ").Append(symbol);
        if (!string.IsNullOrWhiteSpace(summary)) sb.Append('\n').Append(summary.Trim());
        sb.Append('\n');
        var room = Math.Max(0, maxChars - sb.Length);
        sb.Append(content.Length <= room ? content : content[..room]);
        return sb.ToString();
    }
}
