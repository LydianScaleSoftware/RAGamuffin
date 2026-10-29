using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Ocr;

namespace Ragamuffin.Mcp;

public sealed record PageTextInput(
    [property: JsonPropertyName("page"), Description("1-based page number.")] int Page,
    [property: JsonPropertyName("text"), Description("The page's full, corrected text as Markdown. Empty or null removes Claude's text and restores the previous source.")] string? Text,
    [property: JsonPropertyName("note"), Description("Optional: what was wrong or changed, e.g. \"OCR misread the register table\".")] string? Note = null,
    [property: JsonPropertyName("confirm"), Description("true instead of text: the text in use is already correct; keep it and mark the page checked.")] bool Confirm = false);

/// <summary>
/// PDF pages for Claude to check: which pages need a look, each page's image beside every text version the server
/// has, and a place to save Claude's corrected text. Saved text becomes the page's source and the PDF is re-indexed.
/// </summary>
[McpServerToolType]
public sealed class PageTools(RagDbContext db, DocumentReader reader, OcrEngine ocr, FileStore files, IngestQueue queue)
{
    [McpServerTool(Name = "list_pages", ReadOnly = true)]
    [Description("List a PDF's pages with where each page's text comes from (embedded, glm-ocr, layout, claude), its length, and its state: problem (text probably wrong or missing), note (something skipped, text intact), muted (the user set it aside), blank, verified (checked by Claude or the user) or unverified. Starts with the document's health. Use to find pages to check with get_page.")]
    public async Task<string> ListPages(
        [Description("PDF document name (partial names work) or id.")] string document,
        [Description("\"review\" (default): problem pages, then unverified pages only glm-ocr has read. \"problems\": problem pages only. \"notes\": pages with notes or muted items. \"sample\": about 12 unverified pages spread through the document, favouring tables and listings, to check with get_page and set_page_text before verify_document accepts the rest. \"all\": every page.")] string? which = null,
        [Description("Only these pages, e.g. \"1-100\".")] string? pages = null,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, name, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return problem!;
        var range = string.IsNullOrWhiteSpace(pages) ? new TextRange(1, null) : DocumentReader.ParseRange(pages);
        if (range is null) return $"Couldn't read pages=\"{pages}\"; use e.g. \"1-100\".";

        var all = await db.Pages.AsNoTracking().Where(p => p.DocumentId == id).OrderBy(p => p.Page).ToListAsync(ct);
        if (all.Count == 0) return $"{name} has no pages (it isn't a PDF, or it hasn't been indexed yet).";
        var mode = which?.Trim().ToLowerInvariant() switch { "all" => "all", "problems" => "problems", "notes" => "notes", "sample" => "sample", _ => "review" };
        var inRange = all.Where(p => p.Page >= range.Start && p.Page <= (range.End ?? int.MaxValue)).ToList();
        var shown = mode == "sample" ? Sample(inRange)
            : inRange.Where(p => mode switch
                {
                    "all" => true,
                    "problems" => PageHealth.StateOf(p) == PageState.Problem,
                    "notes" => PageHealth.StateOf(p) is PageState.Note or PageState.Muted,
                    _ => NeedsReview(p),
                })
                // Problem pages first: they're the likeliest to be wrong.
                .OrderBy(p => mode == "all" || PageHealth.StateOf(p) != PageState.Problem).ThenBy(p => p.Page).ToList();

        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id, ct);
        var states = all.Select(PageHealth.StateOf).ToList();
        int Count(PageState s) => states.Count(x => x == s);
        var sb = new StringBuilder($"{name}: {all.Count} pages. Health: {PageHealth.Summary(doc)}. " +
            $"{Count(PageState.Problem)} problem, {Count(PageState.Note)} note, {Count(PageState.Muted)} muted by the user, " +
            $"{Count(PageState.Blank)} blank, {Count(PageState.Verified)} verified ({all.Count(p => p.Source == PageSource.Claude)} by Claude), " +
            $"{Count(PageState.Unverified)} unverified; {all.Count(NeedsReview)} need review.\n");
        if (doc.Error is not null && !doc.NoticeMuted) sb.Append($"Notice from the last run: {doc.Error}\n");
        if (mode == "sample")
            sb.Append("Check each sampled page with get_page; confirm (set_page_text confirm: true) or correct it. If they match, " +
                      $"verify_document(\"{name}\") records the document's text as verified by sample.\n");
        sb.Append(mode switch { "all" => "Pages", "problems" => "Problem pages", "notes" => "Pages with notes or muted items", "sample" => "Sample", _ => "Pages needing review" })
            .Append(pages is null ? "" : $" in {pages}").Append($" ({shown.Count}):\n");
        foreach (var p in shown) sb.Append(Describe(p)).Append('\n');
        if (shown.Count > 0) sb.Append($"\nLook at one with get_page(\"{name}\", page=N); save corrected text with set_page_text.");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_page", ReadOnly = true)]
    [Description("Get one PDF page as an image together with every text version the server has for it (embedded, glm-ocr, layout, Claude's), so you can compare the text with the page and correct it.")]
    public async Task<CallToolResult> GetPage(
        [Description("PDF document name (partial names work) or id.")] string document,
        [Description("1-based page number.")] int page,
        [Description("Include the page image (default true). Set false to get only the text versions.")] bool image = true,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, name, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return Text(problem!);
        var p = await db.Pages.AsNoTracking().SingleOrDefaultAsync(x => x.DocumentId == id && x.Page == page, ct);
        if (p is null)
        {
            var count = await db.Pages.CountAsync(x => x.DocumentId == id, ct);
            return Text(count == 0 ? $"{name} has no pages (it isn't a PDF, or it hasn't been indexed yet)." : $"{name} has pages 1-{count}; there's no page {page}.");
        }

        var sb = new StringBuilder($"{name}, page {page}\n{Describe(p)}\n");
        Version(sb, "Claude's text", p.ClaudeText, p.Source == PageSource.Claude, p.ClaudeNote is null ? null : "note: " + p.ClaudeNote);
        Version(sb, "glm-ocr text", p.OcrText, p.Source == PageSource.Ocr, p.OcrModel);
        Version(sb, "Layout-arranged embedded text", p.LayoutText, p.Source == PageSource.Layout, null);
        Version(sb, "Embedded text (from the PDF itself)", p.EmbeddedText, p.Source == PageSource.Embedded, p.SuspectReason is null ? null : "looks wrong: " + p.SuspectReason);
        sb.Append($"\nTo save a corrected version: set_page_text(\"{name}\", pages=[{{page: {page}, text: \"…\"}}]). Give the whole page as Markdown (headings, lists, tables, code blocks), in reading order, without running headers, footers or page numbers.");

        var content = new List<ContentBlock>();
        if (image)
        {
            try
            {
                var png = ocr.RenderPage(await files.ReadAsync(id.Value, ct), page - 1);
                content.Add(ImageContentBlock.FromBytes(png, "image/png"));
            }
            catch (Exception ex)
            {
                sb.Insert(0, $"(The page image couldn't be rendered: {ex.Message})\n");
            }
        }
        content.Add(new TextContentBlock { Text = sb.ToString() });
        return new CallToolResult { Content = content };
    }

    [McpServerTool(Name = "set_page_text", ReadOnly = false, Destructive = false)]
    [Description("Save your corrected or transcribed text for PDF pages, or confirm the text in use is right. It becomes each page's text (source \"claude\"), the other versions are kept, and the PDF is re-indexed. Send several pages per call.")]
    public async Task<string> SetPageText(
        [Description("PDF document name (partial names work) or id.")] string document,
        [Description("The pages to save, each with its full corrected text.")] List<PageTextInput> pages,
        [Description("Re-index after saving (default true). Pass false for all but the last of several calls on one document.")] bool reindex = true,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, name, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return problem!;
        if (pages is not { Count: > 0 }) return "No pages given.";
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        if (doc.ContentType != "pdf") return $"{name} isn't a PDF, so it has no pages. Change its text by adding a new version with add_document.";
        if (doc.Status == DocumentStatus.Ocr) return $"{name} is being OCR'd right now; wait for that to finish (or cancel it in the web UI) first.";

        var numbers = pages.Select(p => p.Page).ToList();
        var rows = await db.Pages.Where(p => p.DocumentId == id && numbers.Contains(p.Page)).ToDictionaryAsync(p => p.Page, ct);
        var missing = numbers.Where(n => !rows.ContainsKey(n)).Distinct().ToList();
        if (missing.Count > 0) return $"{name} has no page {string.Join(", ", missing)}; nothing was saved.";

        var now = DateTimeOffset.UtcNow;
        int saved = 0, removed = 0, confirmed = 0;
        foreach (var input in pages)
        {
            var p = rows[input.Page];
            if (input.Confirm && string.IsNullOrWhiteSpace(input.Text))
            {
                if (p.Source == PageSource.Claude || !PdfPages.HasUsefulText(p.EffectiveText)) continue;
                p.SourceBeforeClaude = p.Source;
                p.ClaudeText = p.EffectiveText;
                p.ClaudeNote = string.IsNullOrWhiteSpace(input.Note) ? "confirmed as correct" : input.Note.Trim();
                p.ClaudeAt = now;
                p.Source = PageSource.Claude;
                confirmed++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                if (p.ClaudeText is null) continue;
                p.Source = p.SourceBeforeClaude ?? (p.OcrText is not null ? PageSource.Ocr : p.EmbeddedSource);
                p.ClaudeText = p.ClaudeNote = null;
                p.ClaudeAt = null;
                p.SourceBeforeClaude = null;
                removed++;
                continue;
            }
            if (p.Source != PageSource.Claude) p.SourceBeforeClaude = p.Source;
            p.ClaudeText = TextSanitizer.Clean(input.Text.Replace("\r\n", "\n").Trim());
            p.ClaudeNote = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
            p.ClaudeAt = now;
            p.Source = PageSource.Claude;
            saved++;
        }
        await db.SaveChangesAsync(ct);
        // Health counts change even when the indexed text doesn't (a confirmed page becomes verified).
        IngestWorker.ApplyPageStats(doc, await db.Pages.Where(p => p.DocumentId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        var result = $"{name}: saved Claude's text for {saved} page{(saved == 1 ? "" : "s")}" +
                     (confirmed > 0 ? $", confirmed {confirmed} as already correct" : "") +
                     (removed > 0 ? $", removed it from {removed}" : "") + ".";
        if (!reindex) return result + " Not re-indexed yet; pass reindex=true on the last call.";
        // Earlier calls with reindex=false may have left changes waiting even if this one only confirmed pages.
        var waiting = saved + removed > 0 ||
                      await db.Pages.AnyAsync(p => p.DocumentId == id && p.ClaudeAt > doc.UpdatedAt && p.ClaudeNote != "confirmed as correct", ct);
        if (!waiting) return result + " The indexed text didn't change, so no re-index is needed.";
        await QueueReindexAsync(db, queue, doc.Id, ct);
        return result + " Re-indexing in the background; search reflects it in a minute or so.";
    }

    internal static async Task QueueReindexAsync(RagDbContext db, IngestQueue queue, Guid id, CancellationToken ct)
    {
        var updated = await db.Documents.Where(d => d.Id == id && d.Status != DocumentStatus.Ocr)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DocumentStatus.Pending)
                .SetProperty(d => d.Error, (string?)null)
                .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (updated > 0) await queue.EnqueueAsync(id, ct);
    }

    /// <summary>
    /// Pages whose text may be wrong: problems (a recorded OCR or layout problem, no text on a page with content,
    /// garbled text), plus unverified pages only glm-ocr has read. Muted, blank and verified pages don't need review;
    /// an old OCR layer that merely looks like one is settled by checking a sample instead.
    /// </summary>
    public static bool NeedsReview(DocumentPage p) => PageHealth.StateOf(p) switch
    {
        PageState.Problem => true,
        PageState.Unverified => p.Source == PageSource.Ocr,
        _ => false,
    };

    /// <summary>
    /// About a dozen unverified pages with text, spread evenly through the document, with tables and listings (the
    /// pages most likely to be read wrong) swapped in where there are any.
    /// </summary>
    public static List<DocumentPage> Sample(IReadOnlyList<DocumentPage> pages, int size = 12)
    {
        var candidates = pages.Where(p => PageHealth.StateOf(p) == PageState.Unverified).OrderBy(p => p.Page).ToList();
        if (candidates.Count <= size) return candidates;
        var picked = new SortedSet<int>();
        var spread = size - size / 3;
        for (var i = 0; i < spread; i++) picked.Add(candidates[(int)((i + 0.5) * candidates.Count / spread)].Page);
        // Tables and listings, also spread through the document rather than the first few.
        var hard = candidates.Where(p => !picked.Contains(p.Page) && LooksTabular(p.EffectiveText)).ToList();
        var wanted = size - picked.Count;
        for (var i = 0; i < Math.Min(wanted, hard.Count); i++)
            picked.Add(hard[(int)((i + 0.5) * hard.Count / Math.Min(wanted, hard.Count))].Page);
        for (var i = 0; picked.Count < size && i < candidates.Count; i += Math.Max(1, candidates.Count / size))
            picked.Add(candidates[i].Page);
        return candidates.Where(p => picked.Contains(p.Page)).ToList();
    }

    /// <summary>Tables, code fences or columns of hex/numbers: the content OCR most often gets wrong.</summary>
    static bool LooksTabular(string? text)
    {
        if (text is null) return false;
        var lines = text.Split('\n');
        var tableRows = lines.Count(l => l.TrimStart().StartsWith('|'));
        var numeric = lines.Count(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 3 } w &&
                                       w.Count(t => t.All(c => char.IsAsciiHexDigit(c) || c is '$' or ':' or '.')) >= w.Length / 2);
        return text.Contains("```") || tableRows >= 3 || numeric >= 5;
    }

    [McpServerTool(Name = "verify_document", ReadOnly = false, Destructive = false)]
    [Description("Record that a PDF's text is verified as a whole, after checking a sample of its pages (list_pages which=\"sample\") against their images and confirming or correcting each with set_page_text. Its remaining unverified pages then count as verified by sample; the web UI shows the document as Verified. Needs at least 10 pages (or every page of a shorter document) checked by Claude. Use only when the sample matched (a few small corrections are fine); if many sampled pages were wrong, report that instead and suggest re-OCR or a full check.")]
    public async Task<string> VerifyDocument(
        [Description("PDF document name (partial names work) or id.")] string document,
        [Description("What the check found, e.g. \"12 sampled pages matched; one table had a misread digit, corrected\".")] string? note = null,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, name, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return problem!;
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        if (doc.ContentType != "pdf") return $"{name} isn't a PDF, so it has no pages to verify.";
        var pages = await db.Pages.AsNoTracking().Where(p => p.DocumentId == id).ToListAsync(ct);
        var textPages = pages.Count(p => PdfPages.HasUsefulText(p.EffectiveText));
        var checkedPages = pages.Where(p => p.Source == PageSource.Claude).ToList();
        var needed = Math.Min(10, textPages);
        if (checkedPages.Count < needed)
            return $"{name}: only {checkedPages.Count} page{(checkedPages.Count == 1 ? " is" : "s are")} checked by Claude; check at least {needed} " +
                   $"first (list_pages(\"{name}\", which=\"sample\") picks them).";
        var confirmed = checkedPages.Count(p => p.ClaudeNote == "confirmed as correct");
        var corrected = checkedPages.Count - confirmed;
        var problems = pages.Count(p => PageHealth.StateOf(p) == PageState.Problem);
        doc.VerifiedAt = DateTimeOffset.UtcNow;
        doc.VerifiedBy = "claude";
        doc.VerifiedNote = $"{checkedPages.Count} pages checked by Claude ({confirmed} confirmed, {corrected} corrected)" +
                           (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}");
        await db.SaveChangesAsync(ct);
        return $"{name}: text recorded as verified by sample — {doc.VerifiedNote}." +
               (problems > 0 ? $" {problems} problem page{(problems == 1 ? "" : "s")} still need attention (list_pages which=\"problems\")." : "");
    }

    static string Describe(DocumentPage p)
    {
        var source = p.Source switch
        {
            PageSource.Ocr => $"glm-ocr ({p.OcrModel ?? "unknown model"})",
            PageSource.Layout => "layout-arranged embedded text",
            PageSource.Claude => "Claude",
            _ => "embedded text",
        };
        var text = p.EffectiveText;
        var state = PageHealth.StateOf(p);
        var sb = new StringBuilder($"- p.{p.Page}: {source}, {(PdfPages.HasUsefulText(text) ? $"{text!.Length:N0} chars" : "no usable text")}; {state.ToString().ToLowerInvariant()}");
        if (state is PageState.Problem or PageState.Muted && PageHealth.ProblemOf(p) is { } what) sb.Append($": {what}");
        else if (p.Issue is not null && state is PageState.Note or PageState.Muted) sb.Append($": {p.Issue}");
        else if (p.Issue is not null && state == PageState.Verified) sb.Append($" (had an issue before it was checked: {p.Issue})");
        if (state == PageState.Muted) sb.Append(" (muted by the user)");
        if (p.SuspectReason is not null && p.SuspectReason != PageHealth.GarbledReason && p.Source is PageSource.Embedded or PageSource.Layout)
            sb.Append($"; text layer: {p.SuspectReason}");
        if (p.ClaudeNote is not null) sb.Append($"; Claude's note: {p.ClaudeNote}");
        return sb.ToString();
    }

    static void Version(StringBuilder sb, string label, string? text, bool inUse, string? detail)
    {
        if (text is null) return;
        sb.Append($"\n=== {label}{(inUse ? " (in use)" : "")}{(detail is null ? "" : $" — {detail}")} ===\n");
        sb.Append(string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim()).Append('\n');
    }

    static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
