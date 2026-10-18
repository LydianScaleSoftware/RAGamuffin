using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Data;

namespace Ragamuffin.Mcp;

/// <summary>What an agent is told about one collection.</summary>
public sealed record CollectionSummary(
    string Name,
    int Documents,
    int PdfPages,
    string? Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyDictionary<string, int> Types,
    IReadOnlyList<string> ExampleTitles);

/// <summary>
/// Describes the library to agents within a token budget: the MCP server instructions (added to the agent's system
/// prompt when it connects) and the list_collections tool's answer.
/// </summary>
public sealed class LibraryGuide(RagDbContext db, IOptions<RagOptions> options)
{
    static (string Text, DateTimeOffset At)? _instructionsCache;

    int Budget => options.Value.Mcp.ContextTokens;

    /// <summary>Conservative estimate (about 3.5 characters per token for English prose and names).</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.5);

    public async Task<string> InstructionsAsync(CancellationToken ct)
    {
        if (_instructionsCache is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Text;
        var text = BuildInstructions(await SummariesAsync(ct), Budget);
        _instructionsCache = (text, DateTimeOffset.UtcNow);
        return text;
    }

    public async Task<string> ListCollectionsAsync(string? collection, CancellationToken ct)
    {
        var all = await SummariesAsync(ct);
        if (!string.IsNullOrWhiteSpace(collection))
        {
            var one = all.Where(s => string.Equals(s.Name, collection.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (one.Count == 0)
                return $"No collection named \"{collection}\". Collections: {string.Join(", ", all.Select(s => s.Name))}.";
            return BuildList(one, Budget, titles: 10);
        }
        return BuildList(all, Budget, titles: 5);
    }

    async Task<List<CollectionSummary>> SummariesAsync(CancellationToken ct)
    {
        var collections = await db.Collections.AsNoTracking().ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Failed)
            .Select(d => new { d.Collection, d.Name, d.ContentType, d.PageCount, d.ChunkCount })
            .ToListAsync(ct);
        return collections
            .Select(c =>
            {
                var mine = docs.Where(d => d.Collection == c.Name).ToList();
                return new CollectionSummary(
                    c.Name,
                    mine.Count,
                    mine.Where(d => d.ContentType == "pdf").Sum(d => d.PageCount),
                    c.Description,
                    c.Keywords,
                    mine.GroupBy(d => d.ContentType).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                    mine.OrderByDescending(d => d.ChunkCount).Select(d => Path.GetFileNameWithoutExtension(d.Name.Split('/').Last())).Take(10).ToList());
            })
            .Where(s => s.Documents > 0)
            .OrderByDescending(s => s.Documents)
            .ToList();
    }

    /// <summary>How much detail to include; later levels are shorter.</summary>
    sealed record Detail(int Keywords, int DescriptionChars, int Titles);

    static readonly Detail[] InstructionLevels =
    [
        new(12, 400, 0), new(8, 250, 0), new(5, 160, 0), new(3, 100, 0), new(0, 80, 0), new(0, 0, 0),
    ];

    /// <summary>The server instructions: when to use the tools and what each collection holds, within the budget.</summary>
    public static string BuildInstructions(IReadOnlyList<CollectionSummary> collections, int budgetTokens)
    {
        var docs = collections.Sum(c => c.Documents);
        var header =
            $"RAGamuffin holds the user's own document library ({docs:N0} documents in {collections.Count} collection{(collections.Count == 1 ? "" : "s")}). " +
            "For questions these documents may answer: call ask_library first (one call, cited answer); " +
            "use search_documents for specific passages, then get_document with pages=/lines=/section= to read around a result. " +
            "Never read whole documents. Cite document and page or lines. Pass `collection` when one clearly fits; list_collections describes each.";
        if (collections.Count == 0)
            return "RAGamuffin searches the user's own document library, which is empty right now.";
        return Fit(header, "Collections:", collections, budgetTokens, InstructionLevels, FormatInstructionLine);
    }

    static readonly Detail[] ListLevels =
    [
        new(20, 600, 10), new(12, 400, 5), new(8, 250, 3), new(5, 160, 2), new(3, 100, 0), new(0, 80, 0), new(0, 0, 0),
    ];

    /// <summary>The list_collections answer: per collection, size, types, description, keywords and example titles.</summary>
    public static string BuildList(IReadOnlyList<CollectionSummary> collections, int budgetTokens, int titles)
    {
        if (collections.Count == 0) return "The library is empty.";
        var levels = ListLevels.Where(l => l.Titles <= titles).ToArray();
        return Fit("", "", collections, budgetTokens, levels, FormatListEntry);
    }

    /// <summary>
    /// Tries each detail level until everything fits; if even the shortest level doesn't, keeps the largest
    /// collections and summarizes the rest in one line.
    /// </summary>
    static string Fit(string header, string listTitle, IReadOnlyList<CollectionSummary> collections, int budget,
        Detail[] levels, Func<CollectionSummary, Detail, string> format)
    {
        string Render(IEnumerable<CollectionSummary> items, Detail d, int omitted)
        {
            var sb = new StringBuilder();
            if (header.Length > 0) sb.Append(header).Append("\n\n");
            if (listTitle.Length > 0) sb.Append(listTitle).Append('\n');
            foreach (var c in items) sb.Append(format(c, d)).Append('\n');
            if (omitted > 0) sb.Append($"- …and {omitted} more collection{(omitted == 1 ? "" : "s")} (call list_collections).\n");
            return sb.ToString().TrimEnd();
        }

        foreach (var level in levels)
        {
            var text = Render(collections, level, 0);
            if (EstimateTokens(text) <= budget) return text;
        }

        var shortest = levels[^1];
        for (var keep = collections.Count - 1; keep >= 1; keep--)
        {
            var text = Render(collections.Take(keep), shortest, collections.Count - keep);
            if (EstimateTokens(text) <= budget) return text;
        }
        var minimal = Render([], shortest, collections.Count);
        return EstimateTokens(minimal) <= budget ? minimal : Truncate(minimal, budget);
    }

    static string FormatInstructionLine(CollectionSummary c, Detail d)
    {
        var sb = new StringBuilder($"- {c.Name} ({c.Documents:N0} docs)");
        var description = Shorten(c.Description, d.DescriptionChars);
        if (description is not null) sb.Append(": ").Append(description);
        if (d.Keywords > 0 && c.Keywords.Count > 0)
            sb.Append(description is null ? ": " : " ").Append("Keywords: ").Append(string.Join(", ", c.Keywords.Take(d.Keywords))).Append('.');
        return sb.ToString();
    }

    static string FormatListEntry(CollectionSummary c, Detail d)
    {
        var types = string.Join(", ", c.Types.Select(t => $"{t.Value} {t.Key}"));
        var sb = new StringBuilder($"## {c.Name}\n{c.Documents:N0} documents ({types})");
        if (c.PdfPages > 0) sb.Append($", {c.PdfPages:N0} PDF pages");
        sb.Append('\n');
        var description = Shorten(c.Description, d.DescriptionChars);
        if (description is not null) sb.Append(description).Append('\n');
        if (d.Keywords > 0 && c.Keywords.Count > 0) sb.Append("Keywords: ").Append(string.Join(", ", c.Keywords.Take(d.Keywords))).Append('\n');
        if (d.Titles > 0 && c.ExampleTitles.Count > 0) sb.Append("Examples: ").Append(string.Join("; ", c.ExampleTitles.Take(d.Titles))).Append('\n');
        return sb.ToString();
    }

    static string? Shorten(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0) return null;
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf(' ', maxChars - 1);
        return text[..(cut > maxChars / 2 ? cut : maxChars - 1)].TrimEnd(',', ';', ' ') + "…";
    }

    static string Truncate(string text, int budgetTokens)
    {
        var maxChars = (int)(budgetTokens * 3.5) - 1;
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}
