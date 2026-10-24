using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;

namespace Ragamuffin.Mcp;

/// <summary>
/// The web UI's switches for agents. With reading off, every MCP tool call returns a short note instead of touching
/// the library, and the server instructions tell agents not to use it. With writing off, the tools that change the
/// library (adding documents, saving page text, re-chunking, deleting) return a note and the rest still work.
/// Both are stored in the settings table.
/// </summary>
public sealed class McpSwitch(IServiceScopeFactory scopes)
{
    const string ReadKey = "mcp.enabled";
    const string WriteKey = "mcp.write";

    public const string OffMessage =
        "RAGamuffin is switched off by the user right now, so nothing was searched. Don't retry; continue without the document library.";

    public const string WriteOffMessage =
        "Changing the RAGamuffin library is switched off by the user right now, so nothing was changed. Searching and reading still work. Tell the user if the change is needed.";

    volatile bool _enabled = true;
    volatile bool _writes = true;

    public bool Enabled => _enabled;

    public bool WritesEnabled => _writes;

    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary>Reads the stored state at startup; with nothing stored, agents may read and write.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key == ReadKey || s.Key == WriteKey).ToListAsync(ct);
        _enabled = rows.FirstOrDefault(r => r.Key == ReadKey)?.Value != "false";
        _writes = rows.FirstOrDefault(r => r.Key == WriteKey)?.Value != "false";
        ChangedAt = rows.Count == 0 ? null : rows.Max(r => r.UpdatedAt);
    }

    public async Task SetAsync(bool? enabled, bool? writes, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in new[] { (ReadKey, enabled), (WriteKey, writes) })
        {
            if (value is null) continue;
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row is null) db.Settings.Add(row = new Setting { Key = key, Value = "" });
            row.Value = value.Value ? "true" : "false";
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        if (enabled is { } e) _enabled = e;
        if (writes is { } w) _writes = w;
        ChangedAt = now;
        LibraryGuide.ClearCache();
    }
}
