using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ragamuffin.Search;

namespace Ragamuffin.Mcp;

/// <summary>
/// ask_library: retrieves passages and has a small local model write a short, cited answer, so an agent gets what it
/// needs in one call instead of many search-read-think rounds. Falls back to plain search results if the model fails.
/// </summary>
public sealed class LibraryAnswerer(HttpClient http, SearchService search, IOptions<RagOptions> options, ILogger<LibraryAnswerer> log)
{
    McpOptions Opt => options.Value.Mcp;

    const int Candidates = 24;

    public async Task<string> AskAsync(string question, string? collection, CancellationToken ct)
    {
        var hits = await search.SearchAsync(question, Candidates, collection, ct);
        var passages = LeanText.Dedupe(LeanText.Merge(hits), question).Take(6).ToList();
        if (passages.Count == 0)
            return $"Nothing in the library matches \"{question}\". Try different words or an exact identifier.";

        if (string.IsNullOrWhiteSpace(Opt.AnswerModel))
            return LeanText.FormatSearch(question, passages, options.Value.Mcp.SearchTokens);

        var context = BuildContext(question, passages, Opt.AnswerContextTokens);
        log.LogDebug("ask_library context for {Question}:\n{Context}", question, context);
        try
        {
            var answer = await GenerateAsync(question, context, null, ct);
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("the model returned an empty answer");
            var unsupported = UnsupportedCalls(answer, passages);
            if (unsupported.Count > 0)
            {
                // One correction round: small models often reach for a plausible call they know from elsewhere.
                var retry = await GenerateAsync(question, context, (answer,
                    $"These names do not appear in the passages: {string.Join(", ", unsupported)}. " +
                    $"Calls the passages do show: {string.Join(", ", CallsIn(context).Take(30))}. " +
                    "Rewrite the answer using only those and standard C library functions (printf, puts, …) where a passage describes " +
                    "standard input or output, or say the passages don't cover that part."), ct);
                var retryUnsupported = UnsupportedCalls(retry, passages);
                log.LogInformation("ask_library retried after unsupported calls {Before}; now {After}", unsupported, retryUnsupported);
                if (!string.IsNullOrWhiteSpace(retry) && retryUnsupported.Count < unsupported.Count)
                    (answer, unsupported) = (retry, retryUnsupported);
            }
            return FormatAnswer(answer, passages, Opt.AnswerModel, unsupported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("ask_library fell back to search results: {Error}", ex.Message);
            return $"(ask_library couldn't get an answer from {Opt.AnswerModel}: {ex.Message}. Search results instead.)\n\n" +
                   LeanText.FormatSearch(question, passages, Opt.SearchTokens);
        }
    }

    /// <summary>
    /// Numbered passages for the answer model, excerpted around the question's terms to fit the budget. Room a short
    /// passage doesn't need goes to the longer ones, so a listing isn't cut off before its last lines.
    /// </summary>
    public static string BuildContext(string question, IReadOnlyList<Passage> passages, int budgetTokens)
    {
        var terms = LeanText.QueryTerms(question);
        var allowance = new int[passages.Count];
        var left = LeanText.Chars(budgetTokens);
        // Fill shortest first: each passage gets its fair share of what's left, or all it needs if that's less.
        foreach (var i in Enumerable.Range(0, passages.Count).OrderBy(i => passages[i].Text.Trim().Length))
        {
            var share = left / (passages.Count - allowance.Count(a => a > 0));
            allowance[i] = Math.Min(passages[i].Text.Trim().Length, Math.Max(300, share));
            left = Math.Max(0, left - allowance[i]);
        }
        var sb = new StringBuilder();
        for (var i = 0; i < passages.Count; i++)
        {
            var p = passages[i];
            sb.Append($"[{i + 1}] {p.DocumentName}" + (p.Location.Length > 0 ? $", {p.Location}" : "") + (p.Heading is { } h ? $" — {h}" : "") + "\n");
            sb.Append(LeanText.Excerpt(p.Text, terms, allowance[i])).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    async Task<string> GenerateAsync(string question, string context, (string Previous, string Feedback)? correction, CancellationToken ct)
    {
        var system = """
            You answer questions using ONLY the numbered passages from the user's document library.
            - Be concise: a short answer, then any code or exact details needed. No preamble.
            - Copy function names, signatures, parameters, constants, header names and code exactly as written.
            - Never use a function, constant or header that does not appear in the passages, even if you know one. If the passages describe another way (for example printing text with printf), use that.
            - Cite passages inline like [2]. Every fact must come from a passage.
            - Prefer adapting a complete worked example from the passages over piecing calls together.
            - If the passages don't answer the question, say so plainly and say what to search for instead.
            """;
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = $"Question: {question}\n\nPassages:\n{context}" });
        if (correction is { } c)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = c.Previous });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = c.Feedback });
        }
        var body = new JsonObject
        {
            ["model"] = Opt.AnswerModel,
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = "10m",
            ["messages"] = messages,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.1,
                ["num_predict"] = Opt.AnswerMaxTokens,
                ["num_ctx"] = Math.Clamp(Opt.AnswerContextTokens + Opt.AnswerMaxTokens + 1024, 4096, 32768),
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Opt.AnswerTimeoutSeconds));
        try
        {
            using var resp = await http.PostAsJsonAsync("api/chat", body, timeout.Token);
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {text}");
            var node = JsonNode.Parse(text);
            return (node?["message"]?["content"]?.GetValue<string>() ?? "").Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {Opt.AnswerTimeoutSeconds}s");
        }
    }

    /// <summary>Language keywords and standard C library functions: manuals describe these ("text written to standard
    /// out") without naming them, so they aren't treated as invented.</summary>
    static readonly HashSet<string> Keywords =
    [
        "if", "while", "for", "switch", "return", "sizeof", "main", "do", "else", "case",
        "printf", "fprintf", "sprintf", "snprintf", "puts", "fputs", "putchar", "putc", "fputc", "scanf", "fscanf", "sscanf",
        "getchar", "getc", "fgetc", "gets", "fgets", "fopen", "fclose", "fread", "fwrite", "fflush", "malloc", "calloc",
        "realloc", "free", "exit", "abs", "atoi", "atol", "atof", "strtol", "strlen", "strcpy", "strncpy", "strcat",
        "strncat", "strcmp", "strncmp", "strchr", "strstr", "memcpy", "memmove", "memset", "memcmp", "sin", "cos", "tan",
        "sqrt", "pow", "floor", "ceil", "fabs", "rand", "srand", "time", "clock",
    ];

    /// <summary>
    /// Names the answer calls like functions (<c>Name(</c>) that appear in none of the retrieved passages: the usual
    /// way a small model goes wrong is inventing a plausible toolbox call. No space before the parenthesis, so prose like
    /// "the position (50, 50)" isn't taken for a call.
    /// </summary>
    static readonly Regex CallPattern = new(@"\b([A-Za-z_]\w{2,})\(");

    /// <summary>Distinct names written like function calls, in order of appearance.</summary>
    public static IEnumerable<string> CallsIn(string text) =>
        CallPattern.Matches(text).Select(m => m.Groups[1].Value).Where(n => !Keywords.Contains(n)).Distinct();

    public static List<string> UnsupportedCalls(string answer, IReadOnlyList<Passage> passages)
    {
        var source = string.Join('\n', passages.Select(p => p.Text));
        return CallsIn(answer).Where(n => !Regex.IsMatch(source, $@"\b{n}\b")).ToList();
    }

    /// <summary>The model's answer followed by a compact source list the agent can read further with get_document.</summary>
    public static string FormatAnswer(string answer, IReadOnlyList<Passage> passages, string model, IReadOnlyList<string>? unsupported = null)
    {
        var sb = new StringBuilder(answer.Trim());
        if (unsupported is { Count: > 0 })
            sb.Append($"\n\nWARNING: not found in the sources, may be invented: {string.Join(", ", unsupported)}. Check with search_documents before using.");
        sb.Append("\n\nSources:");
        for (var i = 0; i < passages.Count; i++)
        {
            var p = passages[i];
            sb.Append($"\n[{i + 1}] {p.DocumentName} ({p.Collection})" + (p.ReadHint.Length > 0 ? $" — get_document(\"{p.DocumentName}\", {p.ReadHint})" : ""));
        }
        sb.Append($"\n(Answer written by {model} from these passages; check a source before relying on exact details.)");
        return sb.ToString();
    }
}
