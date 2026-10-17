using System.Text;

namespace RagServer.Ingestion;

/// <summary>One page's text; <paramref name="Markdown"/> is true when it came from layout analysis or OCR (headings are marked up).</summary>
public sealed record PageText(int Page, string Text, bool Markdown = false);

public sealed record ExtractedDocument(string ContentType, string? Language, string Text, IReadOnlyList<PageText>? Pages);

public sealed class ExtractionException(string message) : Exception(message);

public static class TextExtractor
{
    static readonly HashSet<string> MarkdownExtensions = [".md", ".markdown", ".mdx"];

    static readonly Dictionary<string, string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".fs"] = "fsharp", [".vb"] = "vb",
        [".razor"] = "razor", [".cshtml"] = "razor", [".xaml"] = "xml",
        [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml", [".sln"] = "text", [".slnx"] = "xml",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".vue"] = "vue", [".svelte"] = "svelte",
        [".py"] = "python", [".rb"] = "ruby", [".php"] = "php", [".pl"] = "perl", [".lua"] = "lua",
        [".go"] = "go", [".rs"] = "rust", [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin",
        [".scala"] = "scala", [".swift"] = "swift", [".m"] = "objective-c", [".dart"] = "dart",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp",
        [".s"] = "asm", [".asm"] = "asm",
        [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".ps1"] = "powershell",
        [".sql"] = "sql", [".graphql"] = "graphql", [".proto"] = "protobuf",
        [".json"] = "json", [".yaml"] = "yaml", [".yml"] = "yaml", [".toml"] = "toml", [".ini"] = "ini",
        [".xml"] = "xml", [".html"] = "html", [".htm"] = "html", [".css"] = "css", [".scss"] = "scss",
        [".dockerfile"] = "dockerfile", [".tf"] = "terraform", [".gradle"] = "groovy",
    };

    public static bool IsPdf(string fileName) =>
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static ExtractedDocument Extract(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext == ".pdf")
            return ExtractPdf(bytes);

        var text = DecodeText(bytes)
            ?? throw new ExtractionException($"'{fileName}' looks like a binary file; only text, markdown, source code and PDF are supported.");
        var (type, lang) = Classify(fileName);
        return new(type, lang, text, null);
    }

    /// <summary>Content type and code language implied by a file name.</summary>
    public static (string ContentType, string? Language) Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var baseName = Path.GetFileName(fileName);
        if (ext == ".pdf") return ("pdf", null);
        if (MarkdownExtensions.Contains(ext)) return ("markdown", null);
        if (CodeLanguages.TryGetValue(ext, out var lang)) return ("code", lang);
        if (baseName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return ("code", "dockerfile");
        if (baseName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return ("code", "make");
        return ("text", null);
    }

    static ExtractedDocument ExtractPdf(byte[] bytes)
    {
        var pages = PdfPages.Extract(bytes)
            .Select(p => new Data.DocumentPage { Page = p.Page, EmbeddedText = p.Text })
            .ToList();
        var doc = PdfPages.ToExtracted(pages);
        if (doc.Pages!.Count == 0)
            throw new ExtractionException("PDF contains no extractable text (it may be scanned images).");
        return doc;
    }

    /// <summary>Decodes UTF-8/UTF-16 text (BOM aware). Returns null when the bytes look binary.</summary>
    public static string? DecodeText(byte[] bytes)
    {
        string text;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        else
        {
            var probe = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
            if (probe.IndexOf((byte)0) >= 0)
                return null;
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }
        return Normalize(text);
    }

    static string Normalize(string text) => TextSanitizer.Clean(text.Replace("\r\n", "\n").Replace('\r', '\n'));
}
