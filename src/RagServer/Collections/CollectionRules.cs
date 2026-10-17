using RagServer.Data;

namespace RagServer.Collections;

public sealed record CollectionRename(string From, string To);

/// <summary>Validation for collection names and batch renames, independent of the database.</summary>
public static class CollectionRules
{
    public const int MaxNameLength = 100;

    public static string Normalize(string? name) => (name ?? "").Trim();

    public static bool IsDefault(string name) =>
        string.Equals(Normalize(name), Collection.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns an error message, or null when <paramref name="name"/> is usable for a named collection.</summary>
    public static string? ValidateName(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return "Name is empty.";
        if (n.Length > MaxNameLength) return $"Name is longer than {MaxNameLength} characters.";
        if (n.Any(char.IsControl)) return "Name contains control characters.";
        if (IsDefault(n)) return "\"default\" is reserved for the built-in collection.";
        return null;
    }

    /// <summary>
    /// Checks a batch of renames against the existing collection names. Renames apply together, so swaps
    /// (A→B with B→A) are allowed; the end result must still have unique names. Returns one error per bad entry.
    /// </summary>
    public static List<string> ValidateRenames(IReadOnlyCollection<string> existing, IReadOnlyList<CollectionRename> renames)
    {
        var errors = new List<string>();
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var seenFrom = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in renames)
        {
            var from = Normalize(r.From);
            var to = Normalize(r.To);
            if (IsDefault(from)) { errors.Add("The default collection cannot be renamed."); continue; }
            if (!existingSet.Contains(from)) { errors.Add($"Collection \"{from}\" does not exist."); continue; }
            if (!seenFrom.Add(from)) { errors.Add($"\"{from}\" is renamed more than once."); continue; }
            if (ValidateName(to) is { } err) errors.Add($"\"{from}\" → \"{to}\": {err}");
        }
        if (errors.Count > 0) return errors;

        // Simulate the batch and look for collisions in the resulting set of names.
        var map = renames.ToDictionary(r => Normalize(r.From), r => Normalize(r.To), StringComparer.Ordinal);
        var final = existing.Select(n => map.GetValueOrDefault(n, n));
        foreach (var dup in final.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"More than one collection would be named \"{dup.Key}\".");
        return errors;
    }
}
