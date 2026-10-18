using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Data;

namespace Ragamuffin.Mcp;

/// <summary>An inclusive range of pages or lines; End is null for "to the end".</summary>
public sealed record TextRange(int Start, int? End);

/// <summary>
/// get_document for small models: an outline by default, or just the requested pages, lines or section, always within
/// a token budget and always ending with how to continue.
/// </summary>
public sealed partial class DocumentReader(RagDbContext db, IOptions<RagOptions> options)
{
    int Budget => options.Value.Mcp.DocumentTokens;

    sealed record DocInfo(Guid Id, string Name, string Collection, string ContentType, string Status, string? Error,
        int PageCount, int OcrPages, string? OcrModel);

    public async Task<string> ReadAsync(string nameOrId, string? collection, string? pages, string? lines, string? section, CancellationToken ct)
    {
        var (doc, problem) = await ResolveAsync(nameOrId, collection, ct);
        if (doc is null) return problem!;
        if (doc.Status is not (DocumentStatus.Ready or DocumentStatus.Ocr))
            return $"{doc.Name} is {doc.Status.Replace('_', ' ')}" + (doc.Error is null ? "." : $": {doc.Error}");

        var isPdf = doc.ContentType == "pdf";
        if (!string.IsNullOrWhiteSpace(pages))
        {
            if (!isPdf) return $"{doc.Name} has no pages; use lines=\"1-80\" (or section=) instead.";
            return ParseRange(pages) is { } r ? await ReadPagesAsync(doc, r, ct) : $"Couldn't read pages=\"{pages}\"; use e.g. pages=\"12\" or pages=\"12-14\".";
        }
        if (!string.IsNullOrWhiteSpace(lines))
        {
            if (isPdf) return $"{doc.Name} is a PDF; use pages=\"1-2\" (or section=) instead of lines.";
            return ParseRange(lines) is { } r ? await ReadLinesAsync(doc, r, ct) : $"Couldn't read lines=\"{lines}\"; use e.g. lines=\"120-180\".";
        }
        if (!string.IsNullOrWhiteSpace(section))
            return await ReadSectionAsync(doc, section.Trim(), ct);
        return await OutlineAsync(doc, ct);
    }

    /// <summary>Finds a document by id, exact name, case-insensitive name, file name, or a unique partial match.</summary>
    async Task<(DocInfo? Doc, string? Problem)> ResolveAsync(string nameOrId, string? collection, CancellationToken ct)
    {
        var q = db.Documents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(collection)) q = q.Where(d => d.Collection == collection.Trim());
        var all = await q.Select(d => new DocInfo(d.Id, d.Name, d.Collection, d.ContentType, d.Status, d.Error, d.PageCount, d.OcrPages, d.OcrModel))
            .ToListAsync(ct);

        var key = (nameOrId ?? "").Trim();
        if (Guid.TryParse(key, out var id))
            return all.FirstOrDefault(d => d.Id == id) is { } byId ? (byId, null) : (null, $"No document with id {key}.");

        var candidates = MatchByName(all.Select(d => d.Name).ToList(), key);
        var docs = all.Where(d => candidates.Contains(d.Name)).ToList();
        return docs.Count switch
        {
            0 => (null, $"No document matching \"{key}\"" + (collection is null ? "" : $" in {collection}") + ". Use list_documents(filter=…) to find names."),
            1 => (docs[0], null),
            _ => (null, $"\"{key}\" matches several documents; use one of these names (and collection if needed):\n" +
                        string.Join("\n", docs.Take(8).Select(d => $"- {d.Name} ({d.Collection})"))),
        };
    }

    /// <summary>The best tier of name matches: exact, case-insensitive, file-name, then substring.</summary>
    public static List<string> MatchByName(IReadOnlyList<string> names, string key)
    {
        if (key.Length == 0) return [];
        static string FileName(string n) => n.Split('/').Last();
        var tiers = new Func<string, bool>[]
        {
            n => n == key,
            n => n.Equals(key, StringComparison.OrdinalIgnoreCase),
            n => FileName(n).Equals(FileName(key), StringComparison.OrdinalIgnoreCase),
            n => Path.GetFileNameWithoutExtension(FileName(n)).Equals(Path.GetFileNameWithoutExtension(FileName(key)), StringComparison.OrdinalIgnoreCase),
            n => n.Contains(key, StringComparison.OrdinalIgnoreCase),
        };
        foreach (var tier in tiers)
        {
            var hits = names.Where(tier).Distinct().ToList();
            if (hits.Count > 0) return hits;
        }
        return [];
    }

    [GeneratedRegex(@"^\s*(\d+)\s*(?:(?:-|–|to|\.\.)\s*(\d+)?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();

    /// <summary>Parses "12", "12-14", "12–14", "12 to 14" or "12-" (to the end).</summary>
    public static TextRange? ParseRange(string text)
    {
        var m = RangeRegex().Match(text ?? "");
        if (!m.Success) return null;
        var start = Math.Max(1, int.Parse(m.Groups[1].Value));
        var dash = m.Value.Contains('-') || m.Value.Contains('–') || m.Value.Contains("to", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("..");
        int? end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : dash ? null : start;
        if (end < start) (start, end) = (end.Value, start);
        return new TextRange(start, end);
    }

    /// <summary>A heading found in plain text: 1-based line, its text, and level (1 = chapter-like, 2 = section).</summary>
    public sealed record TextHeading(int Line, string Text, int Level);

    [GeneratedRegex(@"[;{}=()\[\]<>]|[.,:]$")]
    private static partial Regex NotHeadingRegex();

    [GeneratedRegex(@"^(Chapter|Lesson|Part|Appendix|Section)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TopLevelRegex();

    [GeneratedRegex(@"^(Figure|Table|Listing)\s", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionRegex();

    /// <summary>
    /// Finds headings in plain text without Markdown markup: short title-like lines that follow a blank line or are
    /// outdented relative to the text after them. Table-of-contents lines (tab + page number) and figure, table and
    /// listing captions are skipped.
    /// </summary>
    public static List<TextHeading> DetectHeadings(string[] lines)
    {
        static int Indent(string s) => s.Length - s.TrimStart(' ', '\t').Length;
        var headings = new List<TextHeading>();
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var text = raw.Trim();
            if (text.Length is < 3 or > 60 || raw.Contains('\t')) continue;
            if (!(char.IsUpper(text[0]) || char.IsDigit(text[0])) || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) continue;
            if (NotHeadingRegex().IsMatch(text) || CaptionRegex().IsMatch(text)) continue;
            var next = Enumerable.Range(i + 1, Math.Min(3, Math.Max(0, lines.Length - i - 1))).FirstOrDefault(j => lines[j].Trim().Length > 0, -1);
            if (next < 0) continue;
            var blankBefore = i == 0 || lines[i - 1].Trim().Length == 0;
            var outdented = Indent(raw) == 0 && Indent(lines[next]) >= 2;
            if (blankBefore || outdented)
                headings.Add(new TextHeading(i + 1, text, TopLevelRegex().IsMatch(text) ? 1 : 2));
        }
        return headings;
    }

    [GeneratedRegex(@"^\[Page (\d+)\]$", RegexOptions.Multiline)]
    private static partial Regex PageMarkerRegex();

    /// <summary>Splits a PDF's stored text ("[Page n]" markers) into pages; pages without text are absent.</summary>
    public static SortedDictionary<int, string> SplitPages(string text)
    {
        var pages = new SortedDictionary<int, string>();
        var markers = PageMarkerRegex().Matches(text);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            pages[int.Parse(markers[i].Groups[1].Value)] = text[start..end].Trim();
        }
        return pages;
    }

    async Task<string?> TextAsync(Guid id, CancellationToken ct) =>
        await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.Text).SingleAsync(ct);

    async Task<string> ReadPagesAsync(DocInfo doc, TextRange range, CancellationToken ct)
    {
        var pages = SplitPages(await TextAsync(doc.Id, ct) ?? "");
        var last = range.End ?? doc.PageCount;
        var wanted = pages.Where(p => p.Key >= range.Start && p.Key <= last).ToList();
        if (wanted.Count == 0)
            return $"{doc.Name} has no text on pages {range.Start}-{last} (it has {doc.PageCount} pages" +
                   (pages.Count == 0 ? ", none with text yet)." : $"; text on pages {pages.Keys.First()}-{pages.Keys.Last()}).");

        var budget = LeanText.Chars(Budget);
        var sb = new StringBuilder(Header(doc));
        int shownTo = range.Start - 1;
        foreach (var (number, text) in wanted)
        {
            var block = $"\n[Page {number}]\n{text}\n";
            if (sb.Length + block.Length > budget)
            {
                if (shownTo < range.Start) // the first page alone is too long: show as much as fits
                {
                    sb.Append(block[..Math.Max(0, budget - sb.Length - 1)]).Append('…');
                    sb.Append($"\n\n(Page {number} continues; it's longer than one read. Use section= or search_documents for a specific part.)");
                    return sb.ToString();
                }
                break;
            }
            sb.Append(block);
            shownTo = number;
        }
        var next = pages.Keys.FirstOrDefault(p => p > shownTo && p <= last);
        if (next > 0)
            sb.Append($"\n(Showing pages {range.Start}-{shownTo}. Continue with pages=\"{next}-{Math.Min(last, next + Math.Max(1, shownTo - range.Start))}\".)");
        else if (shownTo < doc.PageCount)
            sb.Append($"\n(End of requested pages. The document has {doc.PageCount} pages.)");
        return sb.ToString().TrimEnd();
    }

    async Task<string> ReadLinesAsync(DocInfo doc, TextRange range, CancellationToken ct)
    {
        var all = (await TextAsync(doc.Id, ct) ?? "").Split('\n');
        var last = Math.Min(range.End ?? all.Length, all.Length);
        if (range.Start > all.Length) return $"{doc.Name} has only {all.Length} lines.";

        var budget = LeanText.Chars(Budget);
        var sb = new StringBuilder(Header(doc));
        var shownTo = range.Start - 1;
        for (var n = range.Start; n <= last; n++)
        {
            var line = all[n - 1];
            if (sb.Length + line.Length + 1 > budget) break;
            sb.Append('\n').Append(line);
            shownTo = n;
        }
        if (shownTo < range.Start) // a single enormous line
            sb.Append('\n').Append(all[range.Start - 1].AsSpan(0, Math.Max(0, budget - sb.Length - 1))).Append('…');
        if (shownTo < last)
            sb.Append($"\n\n(Showing lines {range.Start}-{shownTo}. Continue with lines=\"{shownTo + 1}-{Math.Min(all.Length, shownTo + 1 + (shownTo - range.Start))}\".)");
        else if (last < all.Length)
            sb.Append($"\n\n(End of requested lines; the document has {all.Length} lines.)");
        return sb.ToString().TrimEnd();
    }

    async Task<string> ReadSectionAsync(DocInfo doc, string section, CancellationToken ct)
    {
        var chunks = await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == doc.Id && c.HeadingPath != null && EF.Functions.ILike(c.HeadingPath, $"%{section}%"))
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Ordinal, c.HeadingPath, c.Content, c.StartLine, c.EndLine, c.Page })
            .ToListAsync(ct);
        if (chunks.Count == 0 && doc.ContentType != "pdf")
        {
            var textLines = (await TextAsync(doc.Id, ct) ?? "").Split('\n');
            var headings = DetectHeadings(textLines);
            var match = headings.FirstOrDefault(h => h.Text.Contains(section, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var end = headings.FirstOrDefault(h => h.Line > match.Line && h.Level <= match.Level)?.Line - 1 ?? textLines.Length;
                return $"§ {match.Text}\n" + await ReadLinesAsync(doc, new TextRange(match.Line, end), ct);
            }
        }
        if (chunks.Count == 0)
        {
            var outline = await OutlineEntriesAsync(doc, ct);
            return $"No section matching \"{section}\" in {doc.Name}." +
                   (outline.Count == 0 ? " It has no section headings; use pages=/lines= or search_documents." :
                       "\nSections include:\n" + string.Join("\n", outline.Take(15).Select(o => "- " + o.Heading)));
        }

        // Keep the first matching section's run of consecutive chunks.
        var run = new List<(int Ordinal, string? HeadingPath, string Content, int? StartLine, int? EndLine, int? Page)>();
        foreach (var c in chunks)
        {
            if (run.Count > 0 && c.Ordinal != run[^1].Ordinal + 1) break;
            run.Add((c.Ordinal, c.HeadingPath, c.Content, c.StartLine, c.EndLine, c.Page));
        }
        var text = run[0].Content;
        foreach (var c in run.Skip(1)) text = LeanText.JoinOverlapping(text, c.Content);
        var where = run[0].Page is { } p ? (run[^1].Page is { } e && e != p ? $"pages {p}-{e}" : $"page {p}")
            : run[0].StartLine is { } s ? $"lines {s}-{run[^1].EndLine}" : "";

        var budget = LeanText.Chars(Budget);
        var sb = new StringBuilder(Header(doc)).Append($"§ {run[0].HeadingPath}" + (where.Length > 0 ? $" ({where})" : "")).Append('\n');
        if (sb.Length + text.Length <= budget) sb.Append(text);
        else
        {
            sb.Append(text.AsSpan(0, Math.Max(0, budget - sb.Length - 1))).Append('…');
            var resume = run[0].Page is not null ? $"pages=\"{run[^1].Page}\"" : $"lines=\"{run[0].StartLine}-{run[^1].EndLine}\"";
            sb.Append($"\n\n(Section continues; read the rest with {resume}.)");
        }
        var others = chunks.Select(c => c.HeadingPath).Distinct().Skip(1).Take(5).ToList();
        if (others.Count > 0) sb.Append("\n\nOther matching sections: ").Append(string.Join("; ", others));
        return sb.ToString().TrimEnd();
    }

    sealed record OutlineEntry(string Heading, string Location);

    async Task<List<OutlineEntry>> OutlineEntriesAsync(DocInfo doc, CancellationToken ct)
    {
        var chunks = await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == doc.Id && c.HeadingPath != null)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.HeadingPath, c.StartLine, c.Page })
            .ToListAsync(ct);
        var entries = new List<OutlineEntry>();
        string? last = null;
        foreach (var c in chunks)
        {
            if (c.HeadingPath == last) continue;
            last = c.HeadingPath;
            entries.Add(new OutlineEntry(c.HeadingPath!, c.Page is { } p ? $"p.{p}" : c.StartLine is { } s ? $"line {s}" : ""));
        }
        if (entries.Count > 0) return entries;

        var text = await TextAsync(doc.Id, ct) ?? "";
        if (doc.ContentType != "pdf")
            return DetectHeadings(text.Split('\n'))
                .Select(h => new OutlineEntry(h.Level == 1 ? h.Text : "  " + h.Text, $"line {h.Line}"))
                .ToList();

        // PDFs without headings: pages whose first line reads like a heading (chapter and section starts).
        return SplitPages(text)
            .Select(p => (p.Key, First: p.Value.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? ""))
            .Where(p => DetectHeadings(["", p.First, "x"]).Count > 0)
            .Select(p => new OutlineEntry(p.First, $"p.{p.Key}"))
            .ToList();
    }

    async Task<string> OutlineAsync(DocInfo doc, CancellationToken ct)
    {
        var entries = await OutlineEntriesAsync(doc, ct);
        var text = await TextAsync(doc.Id, ct) ?? "";
        var isPdf = doc.ContentType == "pdf";
        var size = isPdf ? $"{doc.PageCount} pages" : $"{text.Split('\n').Length} lines";
        var how = isPdf ? "pages=\"12-14\"" : "lines=\"120-200\"";

        var budget = LeanText.Chars(Budget);
        var sb = new StringBuilder(Header(doc)).Append($"{size}. Read a part with {how} or section=\"<heading>\"; " +
                                                       "or find the right place with search_documents.\n");
        if (entries.Count == 0)
        {
            sb.Append("No section headings were detected. Beginning of the document:\n\n");
            var start = isPdf ? string.Join("\n\n", SplitPages(text).Take(2).Select(p => $"[Page {p.Key}]\n{p.Value}")) : text;
            sb.Append(start.Length > budget - sb.Length ? start[..Math.Max(0, budget - sb.Length - 1)] + "…" : start);
            return sb.ToString().TrimEnd();
        }

        sb.Append("Sections:\n");
        // Prefer top-level headings when everything doesn't fit.
        var fits = entries;
        string Render(IEnumerable<OutlineEntry> list) => string.Join("\n", list.Select(e => $"- {e.Heading}" + (e.Location.Length > 0 ? $" ({e.Location})" : "")));
        if (sb.Length + Render(fits).Length > budget)
            fits = entries.Where(e => !e.Heading.Contains(" > ") && !e.Heading.StartsWith("  ")).ToList() is { Count: > 0 } top ? top : entries;
        var shown = 0;
        foreach (var e in fits)
        {
            var line = $"- {e.Heading}" + (e.Location.Length > 0 ? $" ({e.Location})" : "") + "\n";
            if (sb.Length + line.Length > budget - 80) break;
            sb.Append(line);
            shown++;
        }
        if (shown < entries.Count)
            sb.Append($"(Showing {shown} of {entries.Count} sections" + (fits != entries ? ", top level only" : "") + ".)");
        return sb.ToString().TrimEnd();
    }

    static string Header(DocInfo doc) =>
        $"{doc.Name} · {doc.Collection}" +
        (doc.OcrPages > 0 ? $" · {doc.OcrPages} of {doc.PageCount} pages read by OCR ({doc.OcrModel}); may contain recognition errors" : "") + "\n";
}
