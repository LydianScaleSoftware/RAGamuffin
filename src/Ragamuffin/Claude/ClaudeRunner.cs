using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ragamuffin.Mcp;

namespace Ragamuffin.Claude;

/// <summary>
/// Runs queued Claude jobs one at a time: starts Claude Code headless (claude -p) with only the library's MCP tools and
/// the Skill tool, and turns its stream-json output into the job's activity log and result.
/// </summary>
public sealed class ClaudeRunner(ClaudeJobs jobs, McpSwitch mcpSwitch, IOptions<RagOptions> options, ILogger<ClaudeRunner> log)
    : BackgroundService
{
    const string ToolPrefix = "mcp__rag__";

    /// <summary>The library tools a job may call; everything else is unavailable to it.</summary>
    static readonly string[] Tools =
        ["list_documents", "get_document", "search_documents", "get_chunk", "list_pages", "get_page", "set_page_text", "verify_document", "set_chunks"];

    ClaudeOptions Opt => options.Value.Claude;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await foreach (var job in jobs.Reader.ReadAllAsync(stopping))
        {
            if (job.Finished) continue; // cancelled while queued
            var ct = jobs.Started(job, stopping);
            try
            {
                var problem = !mcpSwitch.Enabled || !mcpSwitch.WritesEnabled
                    ? "Agents can't change the library right now (the Agents read/write switches are off), so Claude couldn't save anything."
                    : null;
                if (problem != null) jobs.Finish(job, "failed", problem);
                else await RunAsync(job, ct);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                jobs.Finish(job, "cancelled", job.Error ?? $"Stopped after {Opt.TimeoutMinutes} minutes (Rag:Claude:TimeoutMinutes).");
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                log.LogError(ex, "Claude job {Id} failed", job.Id);
                jobs.Finish(job, "failed", ex.Message);
            }
        }
    }

    async Task RunAsync(ClaudeJob job, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Command(Opt))
        {
            WorkingDirectory = jobs.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in Arguments(job.Prompt, Opt)) start.ArgumentList.Add(arg);
        // launchd starts the service with a bare environment; Claude Code needs HOME to find its login and skills.
        if (!start.Environment.TryGetValue("HOME", out var home) || string.IsNullOrEmpty(home))
            start.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        job.Log($"Starting Claude Code{(string.IsNullOrWhiteSpace(Opt.Model) ? "" : $" ({Opt.Model})")}…");
        log.LogInformation("Claude job {Id}: {Label} on {Document}", job.Id, job.Label, job.DocumentName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {start.FileName}.");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            Apply(job, line);
        await process.WaitForExitAsync(ct);
        var errors = (await stderr).Trim();

        ct.ThrowIfCancellationRequested();
        if (process.ExitCode == 0 && job.Error is null)
            jobs.Finish(job, "done");
        else
            jobs.Finish(job, "failed", job.Error ?? Explain(job.Result ?? (errors.Length > 0 ? errors : $"Claude Code exited with code {process.ExitCode}.")));
    }

    /// <summary>The claude command: the configured one, else the native installer's ~/.local/bin/claude, else the PATH.</summary>
    public static string Command(ClaudeOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.Command)) return opt.Command;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude");
        return File.Exists(local) ? local : "claude";
    }

    /// <summary>Whether the command can be found, for the web UI.</summary>
    public static bool Available(ClaudeOptions opt)
    {
        var command = Command(opt);
        if (Path.IsPathRooted(command)) return File.Exists(command);
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, command)));
    }

    public static List<string> Arguments(string prompt, ClaudeOptions opt)
    {
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["rag"] = new JsonObject { ["type"] = "http", ["url"] = opt.McpUrl } },
        };
        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json", "--verbose",
            "--mcp-config", mcp.ToJsonString(), "--strict-mcp-config",
            // Built-in tools: only Skill, so the rag-check-pdf skill can load. No shell, no file edits.
            "--tools", "Skill",
            "--permission-mode", "dontAsk",
            "--allowedTools", string.Join(",", Tools.Select(t => ToolPrefix + t).Prepend("Skill")),
        };
        if (!string.IsNullOrWhiteSpace(opt.Model)) args.AddRange(["--model", opt.Model]);
        if (opt.MaxBudgetUsd is { } budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        return args;
    }

    /// <summary>Applies one line of Claude Code's stream-json output to the job.</summary>
    public static void Apply(ClaudeJob job, string line)
    {
        JsonNode? e;
        try { e = JsonNode.Parse(line); }
        catch (JsonException) { return; }
        if (e is null) return;

        switch ((string?)e["type"])
        {
            case "system" when (string?)e["subtype"] == "init":
                job.SessionId = (string?)e["session_id"];
                job.Model = (string?)e["model"];
                break;
            case "assistant":
                job.Turns++;
                foreach (var block in e["message"]?["content"]?.AsArray() ?? [])
                {
                    switch ((string?)block?["type"])
                    {
                        case "text" when !string.IsNullOrWhiteSpace((string?)block!["text"]):
                            job.Log(Shorten(((string)block["text"]!).Trim(), 600));
                            break;
                        case "tool_use":
                            job.ToolCalls++;
                            job.Log("→ " + DescribeTool((string?)block!["name"] ?? "?", block["input"]));
                            break;
                    }
                }
                break;
            case "user":
                foreach (var block in e["message"]?["content"]?.AsArray() ?? [])
                    if ((string?)block?["type"] == "tool_result" && (bool?)block["is_error"] == true)
                        job.Log("  tool error: " + Shorten(ResultText(block["content"]), 300));
                break;
            case "result":
                job.Result = (string?)e["result"];
                if (e["total_cost_usd"] is JsonValue cost && cost.TryGetValue<decimal>(out var usd)) job.CostUsd = usd;
                if ((bool?)e["is_error"] == true || (string?)e["subtype"] is { } sub && sub != "success")
                    job.Error = Explain(job.Result ?? (string?)e["subtype"] ?? "Claude Code reported an error.");
                break;
        }
    }

    /// <summary>One line naming the tool call, e.g. "get_page p.42" or "set_page_text pages 42, 77".</summary>
    public static string DescribeTool(string name, JsonNode? input)
    {
        var tool = name.StartsWith(ToolPrefix) ? name[ToolPrefix.Length..] : name;
        string? Arg(string key) => input?[key] is JsonValue v ? v.ToString() : null;
        return tool switch
        {
            "get_page" => $"get_page p.{Arg("page")}",
            "set_page_text" => $"set_page_text pages {string.Join(", ", (input?["pages"]?.AsArray() ?? []).Select(p => p?["page"]?.ToString()))}",
            "list_pages" => $"list_pages {Arg("which") ?? "review"}{(Arg("pages") is { } p ? $" {p}" : "")}",
            "verify_document" => "verify_document",
            "Skill" => $"load skill {Arg("skill") ?? Arg("command")}",
            "search_documents" => $"search \"{Shorten(Arg("query") ?? "", 80)}\"",
            _ => tool,
        };
    }

    /// <summary>Adds what to do about errors the user can fix, such as an expired login.</summary>
    public static string Explain(string error) =>
        error.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || error.Contains("/login", StringComparison.Ordinal) ||
        error.Contains("OAuth", StringComparison.Ordinal)
            ? $"{error.Trim()} — Claude Code isn't logged in for this account. Run `claude` in a terminal, type /login, then try again."
            : error.Trim();

    static string ResultText(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray a => string.Join(" ", a.Select(b => (string?)b?["text"]).Where(t => t != null)),
        _ => "",
    };

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
