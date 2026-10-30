using System.Text;

namespace Ragamuffin.Claude;

/// <summary>What a Claude job is asked to do with a document.</summary>
public enum ClaudeTask
{
    /// <summary>Correct the pages whose text has a problem (all of them, or the pages given).</summary>
    Problems,
    /// <summary>Check and correct the pages given.</summary>
    Pages,
    /// <summary>Check every page that needs review: problems, then pages only glm-ocr has read.</summary>
    Review,
    /// <summary>Check a sample of pages and, if they match, verify the document.</summary>
    Sample,
    /// <summary>Only the user's own instructions.</summary>
    Custom,
}

public static class ClaudeTasks
{
    public static ClaudeTask? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "problems" => ClaudeTask.Problems,
        "pages" => ClaudeTask.Pages,
        "review" => ClaudeTask.Review,
        "sample" => ClaudeTask.Sample,
        "custom" => ClaudeTask.Custom,
        _ => null,
    };

    /// <summary>A page list such as "12", "3-7" or "12, 40-45".</summary>
    public static bool ValidPages(string pages) =>
        System.Text.RegularExpressions.Regex.IsMatch(pages, @"^\s*\d+(\s*[-–]\s*\d+)?(\s*,\s*\d+(\s*[-–]\s*\d+)?)*\s*$");

    public static string Name(ClaudeTask task) => task.ToString().ToLowerInvariant();

    /// <summary>Short description for the jobs list, e.g. "Fix problem pages 42, 77".</summary>
    public static string Label(ClaudeTask task, string? pages) => task switch
    {
        ClaudeTask.Problems => string.IsNullOrWhiteSpace(pages) ? "Fix problem pages" : $"Fix problem pages {pages}",
        ClaudeTask.Pages => $"Check pages {pages}",
        ClaudeTask.Review => string.IsNullOrWhiteSpace(pages) ? "Check pages needing review" : $"Check pages needing review in {pages}",
        ClaudeTask.Sample => "Check a sample and verify",
        _ => "Custom instructions",
    };

    /// <summary>Tasks that work on a PDF's pages; the rest work on any document.</summary>
    public static bool NeedsPdf(ClaudeTask task) => task != ClaudeTask.Custom;

    /// <summary>
    /// The prompt Claude Code is started with. The document is named by id as well as by name so partial-name matching
    /// can't pick the wrong one.
    /// </summary>
    public static string Prompt(ClaudeTask task, Guid id, string name, bool isPdf, string? pages, string? instructions)
    {
        var doc = $"\"{name}\" (id {id})";
        var range = string.IsNullOrWhiteSpace(pages) ? null : pages.Trim();
        var s = new StringBuilder();
        s.AppendLine("You are working unattended for the user on their RAGamuffin document library, through the rag MCP tools. " +
                     "Nobody can answer questions during this run, so make sensible choices yourself and note them in your report.");
        s.AppendLine();
        if (isPdf && task != ClaudeTask.Custom)
            s.AppendLine("Use the rag-check-pdf skill if it's available; it describes how to check and correct pages. In short: " +
                         "get_page shows a page's image with its current text; compare them and save corrected text with set_page_text " +
                         "(full page text, Markdown for headings and tables, code blocks for listings with spacing and hex values exactly " +
                         "as printed). If a page's text is already right, confirm it with set_page_text too, so it counts as checked. " +
                         "Save several pages per call, with reindex=false on all but the last call. Always pass the document id below.");
        s.AppendLine();
        switch (task)
        {
            case ClaudeTask.Problems:
                s.AppendLine($"Fix the problem pages of {doc}" + (range is null ? "" : $", only those among pages {range}") +
                             ". Run list_pages with which=\"problems\" to find them; each says what went wrong (no text, garbled text, or a region the OCR model failed on, " +
                             "where a rougher reading was used). Transcribe each page from its image and save it.");
                s.AppendLine("When you're done, run list_pages with which=\"problems\" again and confirm none of those pages remain.");
                break;
            case ClaudeTask.Pages:
                s.AppendLine($"Check pages {range} of {doc} against their images, correct anything wrong, and save every page " +
                             "(corrected or confirmed) with set_page_text.");
                break;
            case ClaudeTask.Review:
                s.AppendLine($"Check the pages of {doc} that need review" + (range is null ? "" : $", only those among pages {range}") +
                             ". Run list_pages with which=\"review\" (it may page its results; keep going until it lists none). Check each page against its image and " +
                             "save it, corrected or confirmed, with set_page_text.");
                break;
            case ClaudeTask.Sample:
                s.AppendLine($"Check whether the text of {doc} can be trusted. Run list_pages with which=\"sample\", check each sampled " +
                             "page against its image, and save it (corrected or confirmed) with set_page_text. If the sample matched " +
                             "(a few small corrections are fine), call verify_document with a short note on what you found. If many " +
                             "sampled pages were wrong, don't verify it; say so in your report and suggest re-OCR or a full check.");
                break;
            case ClaudeTask.Custom:
                s.AppendLine($"The document is {doc}. Follow the user's instructions below.");
                break;
        }
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            s.AppendLine();
            s.AppendLine("The user's instructions for this run:");
            s.AppendLine(instructions.Trim());
        }
        s.AppendLine();
        s.Append("Finish with a short report for the user: which pages you changed and what was wrong with each, which you " +
                 "confirmed unchanged, and anything you couldn't fix or weren't sure about.");
        return s.ToString();
    }
}
