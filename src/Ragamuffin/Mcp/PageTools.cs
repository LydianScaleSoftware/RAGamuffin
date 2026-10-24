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
    [Description("List a PDF's pages with where each page's text comes from (embedded, glm-ocr, layout, claude), its length and any reason it looks wrong. Use to find pages to check with get_page.")]
    public async Task<string> ListPages(
        [Description("PDF document name (partial names work) or id.")] string document,
        [Description("\"review\" (default): pages that are empty, look like poor OCR, or were OCR'd by glm-ocr and not yet checked by Claude. \"all\": every page.")] string? which = null,
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
        var review = !string.Equals(which?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var shown = all.Where(p => p.Page >= range.Start && p.Page <= (range.End ?? int.MaxValue))
            .Where(p => !review || NeedsReview(p)).ToList();

        var sb = new StringBuilder($"{name}: {all.Count} pages; {all.Count(NeedsReview)} need review, {all.Count(p => p.Source == PageSource.Claude)} checked by Claude.\n");
        sb.Append(review ? "Pages needing review" : "Pages").Append(pages is null ? "" : $" in {pages}").Append($" ({shown.Count}):\n");
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

    /// <summary>Empty, suspect, or read only by glm-ocr: pages whose text Claude hasn't confirmed and may be wrong.</summary>
    public static bool NeedsReview(DocumentPage p) =>
        p.Source != PageSource.Claude &&
        (!PdfPages.HasUsefulText(p.EffectiveText) || p.SuspectReason is not null || p.Source == PageSource.Ocr);

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
        var sb = new StringBuilder($"- p.{p.Page}: {source}, {(PdfPages.HasUsefulText(text) ? $"{text!.Length:N0} chars" : "no usable text")}");
        if (p.SuspectReason is not null && p.Source != PageSource.Claude) sb.Append($"; embedded text looks wrong ({p.SuspectReason})");
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
