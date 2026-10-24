using Microsoft.Extensions.Options;

namespace Ragamuffin.Mcp;

/// <summary>Reads files the agent names by path, only from the folders allowed by Rag:Mcp:ImportRoots.</summary>
public sealed class LocalFiles(IOptions<RagOptions> options)
{
    public IReadOnlyList<string> Roots
    {
        get
        {
            var configured = options.Value.Mcp.ImportRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Expand).ToList();
            return configured.Count > 0 ? configured : [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
        }
    }

    /// <summary>The file's bytes, or a message saying why it can't be read.</summary>
    public async Task<(byte[]? Bytes, string FullPath, string? Problem)> ReadAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, "", "No path given.");
        path = path.Trim();
        if (!Path.IsPathRooted(path) && path != "~" && !path.StartsWith("~/"))
            return (null, path, $"Give an absolute path (got \"{path}\").");
        var full = Expand(path);
        if (!Roots.Any(r => IsUnder(full, r)))
            return (null, full, $"{full} is outside the folders RAGamuffin may read ({string.Join(", ", Roots)}). The user can add folders with Rag:Mcp:ImportRoots.");
        var info = new FileInfo(full);
        if (!info.Exists) return (null, full, $"No file at {full}.");
        if (info.Length > options.Value.MaxUploadBytes) return (null, full, $"{full} is larger than the upload limit.");
        return (await File.ReadAllBytesAsync(full, ct), full, null);
    }

    static string Expand(string path)
    {
        if (path == "~" || path.StartsWith("~/"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(path);
    }

    static bool IsUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(root, StringComparison.Ordinal) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
