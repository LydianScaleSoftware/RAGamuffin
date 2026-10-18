using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;

namespace Ragamuffin.Mcp;

/// <summary>
/// The web UI's on/off switch for agents. When off, every MCP tool call returns a short note instead of touching the
/// library, and the server instructions tell agents not to use it. The state is stored in the settings table.
/// </summary>
public sealed class McpSwitch(IServiceScopeFactory scopes)
{
    const string Key = "mcp.enabled";

    public const string OffMessage =
        "RAGamuffin is switched off by the user right now, so nothing was searched. Don't retry; continue without the document library.";

    volatile bool _enabled = true;

    public bool Enabled => _enabled;

    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary>Reads the stored state at startup; with nothing stored, agents are allowed.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        _enabled = row?.Value != "false";
        ChangedAt = row?.UpdatedAt;
    }

    public async Task SetAsync(bool enabled, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = Key, Value = "" });
        row.Value = enabled ? "true" : "false";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _enabled = enabled;
        ChangedAt = row.UpdatedAt;
        LibraryGuide.ClearCache();
    }
}
