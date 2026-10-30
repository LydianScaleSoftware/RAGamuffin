using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin.Claude;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Mcp;

namespace Ragamuffin.Api;

public sealed record ClaudeJobRequest(string? Task, string? Pages, string? Instructions);

/// <summary>
/// Correction jobs for Claude Code: the web UI picks a task for a document (fix problem pages, check pages, check a
/// sample and verify, or the user's own instructions), the server builds the prompt and runs claude -p.
/// </summary>
public static class ClaudeEndpoints
{
    public static void MapClaudeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/claude", (IOptions<RagOptions> options, McpSwitch sw) =>
        {
            var opt = options.Value.Claude;
            return Results.Ok(new
            {
                opt.Enabled,
                Command = ClaudeRunner.Command(opt),
                Available = opt.Enabled && ClaudeRunner.Available(opt),
                Model = string.IsNullOrWhiteSpace(opt.Model) ? null : opt.Model,
                AgentsCanWrite = sw.Enabled && sw.WritesEnabled,
            });
        });

        api.MapGet("/claude/jobs", (Guid? documentId, ClaudeJobs jobs) =>
            Results.Ok(jobs.List(documentId).Select(j => Summary(j, activity: false))));

        api.MapGet("/claude/jobs/{id:guid}", (Guid id, ClaudeJobs jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(Summary(job, activity: true)) : Results.NotFound());

        api.MapGet("/claude/jobs/{id:guid}/prompt", (Guid id, ClaudeJobs jobs) =>
            jobs.Get(id) is { Prompt.Length: > 0 } job ? Results.Text(job.Prompt) : Results.NotFound());

        api.MapPost("/claude/jobs/{id:guid}/cancel", (Guid id, ClaudeJobs jobs) =>
            jobs.Cancel(id) ? Results.Ok() : Results.Conflict(new { errors = new[] { "That job has already finished." } }));

        api.MapPost("/documents/{id:guid}/claude", async (Guid id, ClaudeJobRequest req, RagDbContext db, ClaudeJobs jobs,
            IOptions<RagOptions> options, McpSwitch sw, CancellationToken ct) =>
        {
            var opt = options.Value.Claude;
            if (!opt.Enabled) return Results.BadRequest(new { errors = new[] { "Claude jobs are switched off (Rag:Claude:Enabled)." } });
            if (!ClaudeRunner.Available(opt))
                return Results.BadRequest(new { errors = new[] { $"Claude Code wasn't found ({ClaudeRunner.Command(opt)}). Install it or set Rag:Claude:Command." } });
            if (!sw.Enabled || !sw.WritesEnabled)
                return Results.BadRequest(new { errors = new[] { "Turn on Agents read and Agents write first; Claude needs them to save its corrections." } });
            if (ClaudeTasks.Parse(req.Task) is not { } task)
                return Results.BadRequest(new { errors = new[] { "task must be problems, pages, review, sample or custom." } });

            var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return Results.NotFound();
            var isPdf = doc.ContentType == "pdf";
            if (ClaudeTasks.NeedsPdf(task) && !isPdf)
                return Results.BadRequest(new { errors = new[] { "That task works on a PDF's pages; use custom instructions for other documents." } });
            var pages = string.IsNullOrWhiteSpace(req.Pages) ? null : req.Pages.Trim();
            if (pages != null && !ClaudeTasks.ValidPages(pages))
                return Results.BadRequest(new { errors = new[] { "Pages should look like \"12\", \"3-7\" or \"12, 40-45\"." } });
            if (task == ClaudeTask.Pages && pages is null)
                return Results.BadRequest(new { errors = new[] { "Say which pages to check." } });
            if (task == ClaudeTask.Custom && string.IsNullOrWhiteSpace(req.Instructions))
                return Results.BadRequest(new { errors = new[] { "Write the instructions for Claude." } });
            if (jobs.List(id).Any(j => !j.Finished))
                return Results.Conflict(new { errors = new[] { "Claude already has a job queued or running for this document." } });

            var job = jobs.Enqueue(new ClaudeJob
            {
                DocumentId = id,
                DocumentName = doc.Name,
                Task = ClaudeTasks.Name(task),
                Label = ClaudeTasks.Label(task, pages),
                Pages = pages,
                Instructions = string.IsNullOrWhiteSpace(req.Instructions) ? null : req.Instructions.Trim(),
                Prompt = ClaudeTasks.Prompt(task, id, doc.Name, isPdf, pages, req.Instructions),
            });
            return Results.Ok(Summary(job, activity: false));
        });
    }

    static object Summary(ClaudeJob j, bool activity) => new
    {
        j.Id, j.DocumentId, j.DocumentName, j.Task, j.Label, j.Pages, j.Instructions, j.Status,
        j.CreatedAt, j.StartedAt, j.FinishedAt, j.Model, j.SessionId, j.Turns, j.ToolCalls, j.CostUsd, j.Result, j.Error,
        Last = j.ActivitySnapshot().LastOrDefault(),
        Activity = activity ? j.ActivitySnapshot() : null,
    };
}
