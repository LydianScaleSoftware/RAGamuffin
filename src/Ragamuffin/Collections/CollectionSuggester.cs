using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Data;

namespace Ragamuffin.Collections;

public sealed record CollectionSuggestion(string? Description, List<string> Keywords);

public sealed class SuggestionException(string message) : Exception(message);

/// <summary>Drafts a collection's description and keywords with a local model, from its titles and section headings.</summary>
public sealed class CollectionSuggester(HttpClient http, RagDbContext db, IOptions<RagOptions> options, ILogger<CollectionSuggester> log)
{
    const int MaxTitles = 80;
    const int MaxHeadings = 40;

    public async Task<CollectionSuggestion> SuggestAsync(string name, CancellationToken ct)
    {
        var docs = await db.Documents.AsNoTracking().Where(d => d.Collection == name)
            .Select(d => new { d.Name, d.ContentType }).ToListAsync(ct);
        if (docs.Count == 0)
            throw new SuggestionException("The collection has no documents to describe yet.");

        // Top-level section headings that recur across the collection say a lot about its topics.
        var headings = await db.Chunks.AsNoTracking()
            .Where(c => c.Document.Collection == name && c.HeadingPath != null)
            .Select(c => c.HeadingPath!)
            .ToListAsync(ct);
        var topHeadings = headings
            .Select(h => h.Split(" > ")[0].Trim())
            .Where(h => h.Length is > 2 and < 80)
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(MaxHeadings)
            .Select(g => g.Key)
            .ToList();
        var titles = docs.Select(d => Path.GetFileNameWithoutExtension(d.Name.Split('/').Last())).Distinct().Take(MaxTitles).ToList();
        var types = string.Join(", ", docs.GroupBy(d => d.ContentType).Select(g => $"{g.Count()} {g.Key}"));

        var prompt = $$"""
            You write short catalogue entries for collections in a personal document search index. An AI assistant
            will read your entry to decide whether to search this collection, so say concretely what it covers.

            Collection: {{name}}
            Documents: {{docs.Count}} ({{types}})
            Titles:
            {{string.Join("\n", titles.Select(t => "- " + t))}}
            Frequent section headings:
            {{string.Join("\n", topHeadings.Select(h => "- " + h))}}

            Reply with JSON only: {"description": "...", "keywords": ["...", "..."]}
            - description: one or two sentences, at most 200 characters, naming the subject areas, products or
              technologies and the kind of material (manuals, reference, tutorials, source code, ...).
            - keywords: 6 to 12 specific terms someone might ask about (product names, APIs, languages, chips,
              tools). No generic words like "documentation" or "guide".
            """;

        var opt = options.Value.Collections;
        var body = new JsonObject
        {
            ["model"] = opt.SuggestModel,
            ["stream"] = false,
            ["format"] = "json",
            ["think"] = false,
            ["keep_alive"] = "5m",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_predict"] = 600 },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opt.SuggestTimeoutSeconds));
        string content;
        try
        {
            using var resp = await http.PostAsJsonAsync("api/chat", body, timeout.Token);
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            if (!resp.IsSuccessStatusCode)
                throw new SuggestionException($"The model server returned {(int)resp.StatusCode}: {text}");
            content = JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>() ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SuggestionException($"{opt.SuggestModel} didn't answer within {opt.SuggestTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new SuggestionException($"Ollama server unreachable: {ex.Message}");
        }

        var suggestion = Parse(content);
        log.LogInformation("Suggested details for collection {Name} with {Model}", name, opt.SuggestModel);
        return suggestion;
    }

    /// <summary>Reads the model's JSON answer, tolerating code fences and stray text around the object.</summary>
    public static CollectionSuggestion Parse(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new SuggestionException("The model didn't return a JSON object.");
        try
        {
            var node = JsonNode.Parse(content[start..(end + 1)]);
            var description = CollectionRules.NormalizeDescription(node?["description"]?.GetValue<string>());
            var keywords = node?["keywords"] is JsonArray arr
                ? CollectionRules.NormalizeKeywords(arr.Select(k => k?.GetValue<string>() ?? ""))
                : CollectionRules.ParseKeywords(node?["keywords"]?.GetValue<string>());
            return new CollectionSuggestion(description, keywords);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new SuggestionException($"The model's answer wasn't valid JSON: {ex.Message}");
        }
    }
}
