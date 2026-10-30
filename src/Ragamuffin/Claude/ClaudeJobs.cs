using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Ragamuffin.Claude;

/// <summary>One run of Claude Code on a document, queued from the web UI.</summary>
public sealed class ClaudeJob
{
    const int MaxActivity = 400;

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DocumentId { get; init; }
    public string DocumentName { get; init; } = "";
    public string Task { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Pages { get; init; }
    public string? Instructions { get; init; }
    [JsonIgnore] public string Prompt { get; init; } = "";
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Model { get; set; }
    public string? SessionId { get; set; }
    public int Turns { get; set; }
    public int ToolCalls { get; set; }
    public decimal? CostUsd { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public List<string> Activity { get; set; } = [];

    /// <summary>Moved to the older jobs in the web UI.</summary>
    public bool Dismissed { get; set; }

    public bool Finished => Status is "done" or "failed" or "cancelled";

    public void Log(string line)
    {
        lock (Activity)
        {
            Activity.Add(line);
            if (Activity.Count > MaxActivity) Activity.RemoveRange(0, Activity.Count - MaxActivity);
        }
    }

    public List<string> ActivitySnapshot()
    {
        lock (Activity) return [.. Activity];
    }
}

/// <summary>
/// The queue of Claude jobs. One runs at a time (each one works through pages in the library, and two on the same
/// document would fight). Finished jobs are saved as JSON in the working directory's jobs folder and reloaded at startup.
/// </summary>
public sealed class ClaudeJobs
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly ConcurrentDictionary<Guid, ClaudeJob> _jobs = new();
    readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancels = new();
    readonly Channel<ClaudeJob> _queue = Channel.CreateUnbounded<ClaudeJob>();
    readonly ClaudeOptions _opt;
    readonly ILogger<ClaudeJobs> _log;

    public string WorkingDirectory { get; }
    string JobsDirectory => Path.Combine(WorkingDirectory, "jobs");

    public ClaudeJobs(IOptions<RagOptions> options, IHostEnvironment env, ILogger<ClaudeJobs> log)
    {
        _opt = options.Value.Claude;
        _log = log;
        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(options.Value.FilesDirectory, env.ContentRootPath))!;
        WorkingDirectory = Path.GetFullPath(_opt.WorkingDirectory, dataRoot);
        Directory.CreateDirectory(JobsDirectory);
        Load();
    }

    public ChannelReader<ClaudeJob> Reader => _queue.Reader;

    public ClaudeJob Enqueue(ClaudeJob job)
    {
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);
        return job;
    }

    public ClaudeJob? Get(Guid id) => _jobs.GetValueOrDefault(id);

    /// <summary>Newest first.</summary>
    public List<ClaudeJob> List(Guid? documentId = null) => _jobs.Values
        .Where(j => documentId is null || j.DocumentId == documentId)
        .OrderByDescending(j => j.CreatedAt)
        .ToList();

    public CancellationToken Started(ClaudeJob job, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _opt.TimeoutMinutes)));
        _cancels[job.Id] = cts;
        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        return cts.Token;
    }

    /// <summary>Cancels a queued or running job; false if it had already finished.</summary>
    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.Finished) return false;
        if (job.Status == "queued")
        {
            Finish(job, "cancelled", "Cancelled before it started.");
            return true;
        }
        job.Error = "Cancelled by the user.";
        if (_cancels.TryGetValue(id, out var cts)) cts.Cancel();
        return true;
    }

    public void Finish(ClaudeJob job, string status, string? error = null)
    {
        job.Status = status;
        if (error != null) job.Error ??= error;
        job.FinishedAt = DateTimeOffset.UtcNow;
        if (_cancels.TryRemove(job.Id, out var cts)) cts.Dispose();
        Save(job);
        Trim();
    }

    /// <summary>Moves a finished job to (or back from) the older jobs; false if it's missing or still running.</summary>
    public bool Dismiss(Guid id, bool dismissed)
    {
        if (!_jobs.TryGetValue(id, out var job) || !job.Finished) return false;
        job.Dismissed = dismissed;
        Save(job);
        return true;
    }

    void Save(ClaudeJob job)
    {
        try
        {
            File.WriteAllText(Path.Combine(JobsDirectory, $"{job.Id:N}.json"), JsonSerializer.Serialize(job, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Couldn't save Claude job {Id}: {Error}", job.Id, ex.Message);
        }
    }

    void Load()
    {
        foreach (var file in Directory.EnumerateFiles(JobsDirectory, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<ClaudeJob>(File.ReadAllText(file), Json);
                if (job is null) continue;
                // A job saved unfinished was cut short by a restart.
                if (!job.Finished) (job.Status, job.Error) = ("failed", "The service restarted while this job ran.");
                _jobs[job.Id] = job;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Skipping unreadable Claude job {File}: {Error}", file, ex.Message);
            }
        }
        Trim();
    }

    void Trim()
    {
        foreach (var old in _jobs.Values.Where(j => j.Finished).OrderByDescending(j => j.CreatedAt).Skip(Math.Max(1, _opt.KeepJobs)).ToList())
        {
            _jobs.TryRemove(old.Id, out _);
            try { File.Delete(Path.Combine(JobsDirectory, $"{old.Id:N}.json")); } catch (IOException) { }
        }
    }
}
