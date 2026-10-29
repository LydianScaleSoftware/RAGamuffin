using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Ocr;

namespace Ragamuffin.Mcp;

public sealed record ChunkInput(
    [property: JsonPropertyName("start_line"), Description("First line of the chunk (1-based).")] int StartLine,
    [property: JsonPropertyName("end_line"), Description("Last line of the chunk (inclusive).")] int EndLine,
    [property: JsonPropertyName("title"), Description("Short title naming what the chunk is, e.g. \"Listing 10-1: Writing text to the graphics screen\" or \"DrawCString\".")] string? Title = null,
    [property: JsonPropertyName("section"), Description("Section breadcrumb, e.g. \"Chapter 10 > Drawing Text\".")] string? Section = null,
    [property: JsonPropertyName("summary"), Description("One or two sentences on what the chunk covers or does, in plain words someone would search with. Strongly recommended: long chunks are found by it.")] string? Summary = null,
    [property: JsonPropertyName("kind"), Description("prose, section, table, example, listing, code, function, class, reference, glossary or index.")] string? Kind = null,
    [property: JsonPropertyName("symbol"), Description("For code: the function, class or other symbol the chunk defines.")] string? Symbol = null,
    [property: JsonPropertyName("pages"), Description("Pages of the source PDF this came from, e.g. \"112\" or \"112-113\".")] string? Pages = null);

/// <summary>
/// The tools that change the library. They obey the web UI's "Agents can write" switch (enforced by the tool-call
/// filter in Program.cs).
/// </summary>
[McpServerToolType]
public sealed class LibraryWriteTools(RagDbContext db, DocumentReader reader, IngestService ingest, ClaudeIndexer indexer,
    LocalFiles localFiles, FileStore files, OcrCancellation ocrCancellation)
{
    /// <summary>Tool names the write switch blocks (set_page_text lives in <see cref="PageTools"/>).</summary>
    public static readonly HashSet<string> Names = ["import_file", "add_document", "set_chunks", "delete_document", "set_page_text", "verify_document"];

    [McpServerTool(Name = "import_file", ReadOnly = false, Destructive = false)]
    [Description("Add a file from this computer to the library as it is (PDF, Markdown, text or source code), the same as uploading it in the web UI. The server chunks it; PDFs get their pages extracted for checking with list_pages/get_page. A binary file (ROM, disk image, executable) is stored encoded as a hex dump, up to 1 MB.")]
    public async Task<string> ImportFile(
        [Description("Absolute path of the file, e.g. /Users/me/Manuals/Toolbox.pdf.")] string path,
        [Description("Collection to add it to (created if new). Default: the default collection.")] string? collection = null,
        [Description("Name to store it under, e.g. \"Manuals/Toolbox.pdf\". Default: the file's name.")] string? name = null,
        CancellationToken ct = default)
    {
        var (bytes, full, problem) = await localFiles.ReadAsync(path, ct);
        if (bytes is null) return problem!;
        var result = await ingest.UploadAsync(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name, collection ?? "", bytes, ct, allowBinary: true);
        return result.Outcome switch
        {
            "rejected" => $"Not added: {result.Error}",
            "unchanged" => $"{result.Name} ({result.Collection}) is already in the library with the same content.",
            _ => $"{result.Name} {result.Outcome} in {result.Collection}; indexing in the background (id {result.Id}). " +
                 (TextExtractor.DecodeText(bytes) is null && !TextExtractor.IsPdf(result.Name)
                     ? "It's binary, so it's stored ENCODED as a hex dump (offset, 16 bytes, ASCII per line; the original bytes are kept). Search finds byte patterns and embedded strings; you can re-chunk it with set_chunks, giving routines or tables titles and summaries. "
                     : "") +
                 (TextExtractor.IsPdf(result.Name)
                     ? $"In a minute, list_pages(\"{result.Name}\") shows pages that need checking (scans without text show as having none)."
                     : "It's searchable in a few seconds."),
        };
    }

    [McpServerTool(Name = "add_document", ReadOnly = false, Destructive = false)]
    [Description("Add a text document (usually Markdown you wrote, e.g. a PDF manual converted to Markdown, or source code) with the chunks you choose (Claudomatics): each chunk is a range of whole lines with a title and summary, any size up to the limit, so a section, table, listing or whole function stays together. Replaces a document of the same name.")]
    public async Task<string> AddDocument(
        [Description("Name to store it under, ending in its type: .md for Markdown, .c/.cs/.py… for code, .txt for text. E.g. \"Manuals/Toolbox Reference Vol 2.md\".")] string name,
        [Description("The chunks, in order, not overlapping. Lines between chunks may be left out on purpose (e.g. a table of contents).")] List<ChunkInput> chunks,
        [Description("Absolute path of the file to read (preferred for anything long). Give this or content.")] string? path = null,
        [Description("The document's full text, if there's no file. Give this or path.")] string? content = null,
        [Description("Collection to add it to (created if new).")] string? collection = null,
        [Description("The document this was made from, e.g. the PDF a Markdown conversion came from. Its own chunks then drop out of search so results aren't duplicated; it stays in the library for get_page.")] string? source_document = null,
        CancellationToken ct = default)
    {
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var (read, _, problem) = await localFiles.ReadAsync(path, ct);
            if (read is null) return problem!;
            bytes = read;
        }
        else if (!string.IsNullOrEmpty(content)) bytes = Encoding.UTF8.GetBytes(content);
        else return "Give the document's text with path= (a file on this computer) or content=.";

        Guid? sourceId = null;
        string? sourceName = null;
        if (!string.IsNullOrWhiteSpace(source_document))
        {
            var (id, srcName, problem) = await reader.ResolveIdAsync(source_document, null, ct);
            if (id is null) return "source_document: " + problem;
            (sourceId, sourceName) = (id, srcName);
        }

        if (ToSpecs(chunks, out var specs) is { } specError) return specError;
        try
        {
            var r = await indexer.AddAsync(name, collection ?? "", bytes, specs, sourceId, ct);
            var sb = new StringBuilder($"{r.Document.Name} {r.Outcome} in {r.Document.Collection} with {r.Chunks} chunks; searchable now.");
            if (sourceName is not null) sb.Append($" Linked to its source {sourceName}, whose own chunks are now left out of search.");
            AppendGaps(sb, r.Gaps);
            return sb.ToString();
        }
        catch (ChunkPlanException ex)
        {
            return "Nothing was added. Fix these and call again:\n- " + string.Join("\n- ", ex.Errors);
        }
    }

    [McpServerTool(Name = "set_chunks", ReadOnly = false, Destructive = false)]
    [Description("Replace a text document's chunks with ones you choose (line ranges with titles and summaries), without re-sending the text. Line numbers are the document's own; read them with get_document(lines=…).")]
    public async Task<string> SetChunks(
        [Description("Document name (partial names work) or id.")] string document,
        [Description("The new chunks, in order, not overlapping.")] List<ChunkInput> chunks,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, _, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return problem!;
        if (ToSpecs(chunks, out var specs) is { } specError) return specError;
        var doc = await db.Documents.SingleAsync(d => d.Id == id, ct);
        try
        {
            var r = await indexer.SetChunksAsync(doc, specs, ct);
            var sb = new StringBuilder($"{doc.Name} now has {r.Chunks} chunks; searchable now.");
            AppendGaps(sb, r.Gaps);
            return sb.ToString();
        }
        catch (ChunkPlanException ex)
        {
            return "Nothing was changed. Fix these and call again:\n- " + string.Join("\n- ", ex.Errors);
        }
    }

    [McpServerTool(Name = "delete_document", ReadOnly = false, Destructive = true)]
    [Description("Remove a document, its chunks and its stored file from the library. Only when the user asked for it, or to remove a document you added by mistake.")]
    public async Task<string> DeleteDocument(
        [Description("Exact document name or id.")] string document,
        [Description("Collection, if the name exists in several.")] string? collection = null,
        CancellationToken ct = default)
    {
        var (id, name, problem) = await reader.ResolveIdAsync(document, collection, ct);
        if (id is null) return problem!;
        // Deleting needs the exact name (or id): a partial name that happens to match one document isn't enough.
        if (!Guid.TryParse(document.Trim(), out _) && !string.Equals(name, document.Trim(), StringComparison.Ordinal))
            return $"To delete, give the exact name \"{name}\" (or its id); nothing was deleted.";
        ocrCancellation.Cancel(id.Value);
        var deleted = await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) return $"{name} was already gone.";
        files.Delete(id.Value);
        return $"Deleted {name}.";
    }

    static string? ToSpecs(List<ChunkInput>? chunks, out List<ChunkSpec> specs)
    {
        specs = [];
        if (chunks is not { Count: > 0 }) return "No chunks given.";
        var errors = new List<string>();
        foreach (var (c, i) in chunks.Select((c, i) => (c, i)))
        {
            int? page = null, pageEnd = null;
            if (!string.IsNullOrWhiteSpace(c.Pages))
            {
                if (DocumentReader.ParseRange(c.Pages) is { } r) (page, pageEnd) = (r.Start, r.End ?? r.Start);
                else errors.Add($"Chunk {i + 1}: couldn't read pages=\"{c.Pages}\"; use e.g. \"112\" or \"112-113\".");
            }
            specs.Add(new ChunkSpec(c.StartLine, c.EndLine, c.Title, c.Section, c.Summary,
                string.IsNullOrWhiteSpace(c.Kind) ? null : c.Kind.Trim().ToLowerInvariant(), c.Symbol, page,
                pageEnd == page ? null : pageEnd));
        }
        return errors.Count == 0 ? null : "Nothing was changed:\n- " + string.Join("\n- ", errors);
    }

    static void AppendGaps(StringBuilder sb, List<LineGap> gaps)
    {
        if (gaps.Count == 0) return;
        sb.Append($"\nLines with text that no chunk covers ({gaps.Count}): ")
            .Append(string.Join(", ", gaps.Take(20).Select(g => g.StartLine == g.EndLine ? $"{g.StartLine}" : $"{g.StartLine}-{g.EndLine}")))
            .Append(gaps.Count > 20 ? ", …" : "")
            .Append(". They aren't searchable; that's fine if you skipped them on purpose (a table of contents, say).");
    }
}
