using Microsoft.EntityFrameworkCore;
using RagServer.Data;
using RagServer.Ingestion;

namespace RagServer.Collections;

public sealed record CollectionInfo(string Name, bool IsDefault, int Documents, int Chunks, DateTimeOffset CreatedAt);

public sealed record CreateResult(string Name, string Outcome, string? Error = null);

public sealed record RemoveResult(string Name, int DocumentsMoved, int DocumentsDeleted, string? Error = null);

public enum RemoveMode { MoveToDefault, DeleteDocuments }

public sealed class CollectionService(RagDbContext db, FileStore files)
{
    public async Task<List<CollectionInfo>> ListAsync(CancellationToken ct)
    {
        var stats = await db.Documents.GroupBy(d => d.Collection)
            .Select(g => new { Name = g.Key, Documents = g.Count(), Chunks = g.Sum(d => d.ChunkCount) })
            .ToDictionaryAsync(s => s.Name, ct);
        var rows = await db.Collections.AsNoTracking().ToListAsync(ct);

        return rows
            .Select(c => new CollectionInfo(c.Name, c.Name == Collection.Default,
                stats.GetValueOrDefault(c.Name)?.Documents ?? 0,
                stats.GetValueOrDefault(c.Name)?.Chunks ?? 0,
                c.CreatedAt))
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<CreateResult>> CreateAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var results = new List<CreateResult>();
        var existing = await db.Collections.Select(c => c.Name).ToListAsync(ct);
        var taken = existing.ToHashSet(StringComparer.Ordinal);

        foreach (var raw in names)
        {
            var name = CollectionRules.Normalize(raw);
            if (CollectionRules.ValidateName(name) is { } error) { results.Add(new(name, "rejected", error)); continue; }
            if (!taken.Add(name)) { results.Add(new(name, "exists")); continue; }
            db.Collections.Add(new Collection { Name = name, CreatedAt = DateTimeOffset.UtcNow });
            results.Add(new(name, "created"));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Applies all renames in one transaction, or none of them if any is invalid.</summary>
    public async Task<List<string>> RenameAsync(IReadOnlyList<CollectionRename> renames, CancellationToken ct)
    {
        renames = renames
            .Select(r => new CollectionRename(CollectionRules.Normalize(r.From), CollectionRules.Normalize(r.To)))
            .Where(r => r.From != r.To)
            .ToList();
        if (renames.Count == 0) return [];

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var existing = await db.Collections.Select(c => c.Name).ToListAsync(ct);
        var errors = CollectionRules.ValidateRenames(existing, renames);
        if (errors.Count > 0) return errors;

        // Two passes through temporary names so swaps don't collide on the unique key.
        // Each update cascades to documents.collection.
        var temps = renames.ToDictionary(r => r.From, _ => $"__rename_{Guid.NewGuid():N}");
        foreach (var r in renames)
            await db.Collections.Where(c => c.Name == r.From)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, temps[r.From]), ct);
        foreach (var r in renames)
            await db.Collections.Where(c => c.Name == temps[r.From])
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, r.To), ct);

        await tx.CommitAsync(ct);
        return [];
    }

    public async Task<List<RemoveResult>> RemoveAsync(IEnumerable<string> names, RemoveMode mode, CancellationToken ct)
    {
        var results = new List<RemoveResult>();
        var deletedFiles = new List<Guid>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var raw in names.Select(CollectionRules.Normalize).Distinct())
        {
            if (CollectionRules.IsDefault(raw)) { results.Add(new(raw, 0, 0, "The default collection cannot be removed.")); continue; }
            if (!await db.Collections.AnyAsync(c => c.Name == raw, ct)) { results.Add(new(raw, 0, 0, "Collection does not exist.")); continue; }

            int moved = 0, deleted = 0;
            if (mode == RemoveMode.DeleteDocuments)
            {
                deletedFiles.AddRange(await db.Documents.Where(d => d.Collection == raw).Select(d => d.Id).ToListAsync(ct));
                deleted = await db.Documents.Where(d => d.Collection == raw).ExecuteDeleteAsync(ct);
            }
            else
            {
                var clashes = await db.Documents
                    .Where(d => d.Collection == raw &&
                                db.Documents.Any(o => o.Collection == Collection.Default && o.Name == d.Name))
                    .Select(d => d.Name).Take(5).ToListAsync(ct);
                if (clashes.Count > 0)
                {
                    results.Add(new(raw, 0, 0,
                        $"Default already has documents with the same names ({string.Join(", ", clashes)}); delete or rename them first, or remove with their documents."));
                    continue;
                }
                moved = await db.Documents.Where(d => d.Collection == raw)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Collection, Collection.Default)
                        .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow), ct);
            }

            await db.Collections.Where(c => c.Name == raw).ExecuteDeleteAsync(ct);
            results.Add(new(raw, moved, deleted));
        }

        await tx.CommitAsync(ct);
        foreach (var id in deletedFiles) files.Delete(id);
        return results;
    }

    /// <summary>Creates the collection if needed (used by API uploads that name a new collection).</summary>
    public Task EnsureExistsAsync(string name, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO collections (name, created_at) VALUES ({name}, now()) ON CONFLICT (name) DO NOTHING", ct);
}
