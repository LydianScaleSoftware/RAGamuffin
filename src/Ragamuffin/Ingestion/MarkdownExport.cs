using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Data;

namespace Ragamuffin.Ingestion;

/// <summary>A page whose text is probably wrong or missing, reported before an export so the user can decide.</summary>
public sealed record ExportProblem(int Page, string Problem, bool Muted);

/// <summary>A PDF's page text as one Markdown file, with chunks that follow its pages.</summary>
public sealed record MarkdownExport(
    string Text, List<ChunkSpec> Chunks, int Pages, int PagesWithText, int Verified, int Unverified, int Blank,
    List<ExportProblem> Problems);

/// <summary>
/// Builds a Markdown export of a PDF from the text its pages already have (embedded, layout, OCR or Claude's), without
/// reading the pages again. Each page starts with a <c>&lt;!-- page N --&gt;</c> marker, noting pages without text or
/// with problems, and the export's chunks never span pages, so search can cite PDF pages and the PDF's own chunks drop
/// out of search for the pages the export covers.
/// </summary>
public static partial class MarkdownExports
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    /// <summary>"Manuals/Toolbox.pdf" → "Manuals/Toolbox.md".</summary>
    public static string DefaultName(string pdfName) =>
        (pdfName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? pdfName[..^4] : pdfName) + ".md";

    public static MarkdownExport Build(string pdfName, IReadOnlyList<DocumentPage> pages, int targetChars, int maxChars, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"<!-- Markdown export of \"{Comment(pdfName)}\", made by RAGamuffin from its page text on {now:yyyy-MM-dd}. " +
            "Each \"page N\" marker starts a page of the PDF. -->",
            "",
        };
        var chunks = new List<ChunkSpec>();
        var problems = new List<ExportProblem>();
        var headings = new string?[6];
        int withText = 0, verified = 0, unverified = 0, blank = 0;

        foreach (var p in pages.OrderBy(p => p.Page))
        {
            var state = PageHealth.StateOf(p);
            var problem = state is PageState.Problem or PageState.Muted ? PageHealth.ProblemOf(p) : null;
            if (problem is not null) problems.Add(new ExportProblem(p.Page, problem, state == PageState.Muted));
            switch (state)
            {
                case PageState.Verified: verified++; break;
                case PageState.Blank: blank++; break;
                case PageState.Unverified or PageState.Note: unverified++; break;
            }

            // Whatever text the page has goes in, even a scrap too short to count as the page's text.
            var note = state == PageState.Blank ? "blank"
                : problem is not null ? Comment(problem.TrimEnd('.'))
                : PdfPages.HasUsefulText(p.EffectiveText) ? null : "no text";
            lines.Add(note is null ? $"<!-- page {p.Page} -->" : $"<!-- page {p.Page}: {note} -->");
            lines.Add("");
            if (string.IsNullOrWhiteSpace(p.EffectiveText)) continue;

            withText++;
            var body = PageLines(p.EffectiveText!, markdown: p.Source != PageSource.Embedded);
            var first = lines.Count + 1;
            lines.AddRange(body);
            lines.Add("");
            chunks.AddRange(ChunkPage(body, first, p.Page, headings, targetChars, maxChars));
        }

        return new MarkdownExport(string.Join('\n', lines).TrimEnd() + "\n", chunks, pages.Count, withText, verified, unverified, blank, problems);
    }

    /// <summary>
    /// The page's lines. Plain (embedded) text has a leading # or ``` escaped so it isn't read as a heading or a code
    /// fence; a code fence a page leaves open is closed so it can't swallow the pages after it.
    /// </summary>
    static List<string> PageLines(string text, bool markdown)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (!markdown)
            return lines.Select(l => l.TrimStart() is var t && (t.StartsWith('#') || t.StartsWith("```") || t.StartsWith("~~~"))
                ? l[..(l.Length - t.Length)] + "\\" + t : l).ToList();
        if (lines.Count(IsFence) % 2 == 1) lines.Add("```");
        return lines;
    }

    static bool IsFence(string line) => line.TrimStart() is var t && (t.StartsWith("```") || t.StartsWith("~~~"));

    /// <summary>
    /// Cuts one page into chunks of whole paragraphs (code fences kept whole) of about <paramref name="target"/>
    /// characters, starting a new chunk at a heading. <paramref name="headings"/> carries the breadcrumb across pages.
    /// </summary>
    static IEnumerable<ChunkSpec> ChunkPage(List<string> body, int firstLine, int page, string?[] headings, int target, int max)
    {
        // Paragraph blocks: (first index, last index, characters, section once its heading is applied).
        var blocks = new List<(int Start, int End, int Chars, string? Section, bool Heading)>();
        var start = -1;
        var inFence = false;
        var heading = false;
        for (var i = 0; i <= body.Count; i++)
        {
            var line = i < body.Count ? body[i] : "";
            var m = !inFence && i < body.Count ? HeadingRegex().Match(line) : Match.Empty;
            if ((line.Length == 0 && !inFence) || m.Success)
            {
                if (start >= 0) blocks.Add((start, i - 1, Chars(body, start, i - 1), Breadcrumb(headings), heading));
                start = -1;
                heading = false;
                if (!m.Success) continue;
                var level = m.Groups[1].Length;
                headings[level - 1] = m.Groups[2].Value;
                for (var h = level; h < headings.Length; h++) headings[h] = null;
                heading = true;
            }
            if (i == body.Count) break;
            if (start < 0) start = i;
            if (IsFence(line)) inFence = !inFence;
        }

        int? from = null;
        int to = 0, size = 0;
        string? section = null;
        var onlyHeadings = false; // the chunk so far is headings alone, which stay with the section they open
        foreach (var b in blocks)
        {
            if (from is not null && (size + b.Chars > max ||
                                     (b.Heading ? !onlyHeadings : size + b.Chars > target && size >= target / 4)))
            {
                yield return Spec(from.Value, to);
                from = null;
            }
            if (from is null) { from = b.Start; size = 0; onlyHeadings = true; }
            if (onlyHeadings) section = b.Section;
            onlyHeadings &= b.Heading;
            to = b.End;
            size += b.Chars;
        }
        if (from is not null) yield return Spec(from.Value, to);

        ChunkSpec Spec(int a, int z) => new(firstLine + a, firstLine + z, Section: section, Page: page);
    }

    static int Chars(List<string> body, int a, int z) => Enumerable.Range(a, z - a + 1).Sum(i => body[i].Length + 1);

    static string? Breadcrumb(string?[] headings) =>
        headings.Any(h => h != null) ? string.Join(" > ", headings.Where(h => h != null)) : null;

    /// <summary>Text safe inside an HTML comment.</summary>
    static string Comment(string s) => s.Replace("--", "–").Replace('\n', ' ');
}

/// <summary>Makes, saves and refreshes Markdown exports of PDFs.</summary>
public sealed class MarkdownExporter(RagDbContext db, ClaudeIndexer indexer, IOptions<RagOptions> options, ILogger<MarkdownExporter> log)
{
    public async Task<(Document Pdf, MarkdownExport Export)?> BuildAsync(Guid pdfId, CancellationToken ct)
    {
        var pdf = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == pdfId && d.ContentType == "pdf", ct);
        if (pdf is null) return null;
        var pages = await db.Pages.AsNoTracking().Where(p => p.DocumentId == pdfId).OrderBy(p => p.Page).ToListAsync(ct);
        var opt = options.Value;
        return (pdf, MarkdownExports.Build(pdf.Name, pages, opt.ChunkTargetChars, opt.ChunkMaxChars, DateTimeOffset.UtcNow));
    }

    /// <summary>Saves the export as a Markdown document linked to its PDF, replacing one of the same name.</summary>
    public async Task<ClaudeIndexResult> SaveAsync(Guid pdfId, string name, string? collection, CancellationToken ct)
    {
        if (await BuildAsync(pdfId, ct) is not var (pdf, export))
            throw new ChunkPlanException(["That document isn't a PDF."]);
        if (export.Chunks.Count == 0)
            throw new ChunkPlanException([$"{pdf.Name} has no page text to export yet. Run OCR first."]);
        var bytes = Encoding.UTF8.GetBytes(export.Text);
        return await indexer.AddAsync(name, collection ?? pdf.Collection, bytes, export.Chunks, pdf.Id, ct, ChunkSources.Export);
    }

    /// <summary>
    /// Re-makes the PDF's exports after its pages changed (OCR, layout, or Claude's corrections), so search, which
    /// prefers an export to the PDF's own chunks, never serves the old text.
    /// </summary>
    public async Task RefreshAsync(Guid pdfId, CancellationToken ct)
    {
        var exports = await db.Documents.AsNoTracking()
            .Where(d => d.SourceDocumentId == pdfId && d.ChunkSource == ChunkSources.Export)
            .Select(d => new { d.Name, d.Collection }).ToListAsync(ct);
        foreach (var e in exports)
        {
            try
            {
                await SaveAsync(pdfId, e.Name, e.Collection, ct);
            }
            catch (Exception ex) when (ex is ChunkPlanException or InvalidOperationException or HttpRequestException)
            {
                log.LogWarning("Couldn't refresh the Markdown export {Name}: {Error}", e.Name, ex.Message);
            }
        }
    }
}
