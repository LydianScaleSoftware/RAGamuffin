using Microsoft.Extensions.Options;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Mcp;
using Ragamuffin.Collections;

namespace Ragamuffin.Tests;

public class ChunkPlanTests
{
    static readonly string[] Lines =
    [
        "# Drawing Text",                          // 1
        "",                                         // 2
        "QuickDraw II draws text at the pen.",      // 3
        "",                                         // 4
        "```c",                                     // 5
        "int main (void)",                          // 6
        "{",                                        // 7
        "    startgraph(320);",                     // 8
        "    DrawCString(\"Hello, world.\");",      // 9
        "    endgraph();",                          // 10
        "}",                                        // 11
        "```",                                      // 12
        "",                                         // 13
        "Table of contents ........ 3",             // 14
    ];

    [Fact]
    public void Cuts_whole_line_ranges_and_reports_uncovered_text()
    {
        var r = ChunkPlan.Validate(Lines, [new ChunkSpec(1, 3, Title: "Drawing Text"), new ChunkSpec(5, 12, Kind: "listing")], 10_000);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal("# Drawing Text\n\nQuickDraw II draws text at the pen.", r.Chunks[0].Content);
        Assert.StartsWith("```c\nint main (void)", r.Chunks[1].Content);
        Assert.EndsWith("}\n```", r.Chunks[1].Content);
        // Blank lines 4 and 13 between chunks don't count; the table-of-contents line does.
        Assert.Equal([new LineGap(14, 14)], r.Gaps);
    }

    [Fact]
    public void Page_markers_between_chunks_are_not_reported_as_uncovered()
    {
        string[] lines = ["# Chapter 16", "", "<!-- page 102 -->", "## DrawChar", "Draws a character.", "", "<!-- page 103 -->", "## DrawCString", "Draws a C string."];
        var r = ChunkPlan.Validate(lines, [new ChunkSpec(4, 5), new ChunkSpec(8, 9)], 10_000);
        Assert.Equal([new LineGap(1, 1)], r.Gaps);
    }

    [Fact]
    public void A_chunk_may_be_any_size_up_to_the_limit()
    {
        Assert.True(ChunkPlan.Validate(Lines, [new ChunkSpec(1, 14)], 10_000).Ok);
        var tooBig = ChunkPlan.Validate(Lines, [new ChunkSpec(1, 14)], 50);
        Assert.Contains(tooBig.Errors, e => e.Contains("over the 50-character limit"));
    }

    [Theory]
    [InlineData(0, 3, "start_line must be at least 1")]
    [InlineData(5, 4, "end_line is before start_line")]
    [InlineData(10, 20, "past the end of the document (14 lines)")]
    [InlineData(2, 2, "the lines are blank")]
    public void Rejects_bad_ranges(int start, int end, string error)
    {
        var r = ChunkPlan.Validate(Lines, [new ChunkSpec(start, end)], 10_000);
        Assert.Contains(r.Errors, e => e.Contains(error));
    }

    [Fact]
    public void Chunks_must_be_in_order_without_overlap()
    {
        var overlap = ChunkPlan.Validate(Lines, [new ChunkSpec(1, 6), new ChunkSpec(5, 12)], 10_000);
        Assert.Contains(overlap.Errors, e => e.Contains("overlaps or comes before the previous chunk, which ends at line 6"));
        var reversed = ChunkPlan.Validate(Lines, [new ChunkSpec(5, 12), new ChunkSpec(1, 3)], 10_000);
        Assert.False(reversed.Ok);
    }

    [Theory]
    [InlineData("Function", false)]
    [InlineData("a function", false)]
    [InlineData("function", true)]
    [InlineData("api-reference", true)]
    public void Kinds_are_short_lowercase_words(string kind, bool ok)
    {
        Assert.Equal(ok, ChunkPlan.Validate(Lines, [new ChunkSpec(1, 3, Kind: kind)], 10_000).Ok);
    }

    [Fact]
    public void Page_ranges_must_run_forwards()
    {
        Assert.True(ChunkPlan.Validate(Lines, [new ChunkSpec(1, 3, Page: 112, PageEnd: 113)], 10_000).Ok);
        Assert.False(ChunkPlan.Validate(Lines, [new ChunkSpec(1, 3, Page: 113, PageEnd: 112)], 10_000).Ok);
    }
}

public class ClaudeEmbeddingTextTests
{
    [Fact]
    public void What_describes_a_chunk_comes_first_and_the_content_is_cut_to_fit()
    {
        var content = string.Concat(Enumerable.Repeat("x", 500));
        var text = Chunker.EmbeddingText("Manuals/Toolbox.md", null, "Chapter 10 > Drawing Text", 112,
            "Listing 10-1", "DrawCString", "Writes Hello World with DrawCString.", content, 200);
        Assert.StartsWith("Manuals/Toolbox.md > Chapter 10 > Drawing Text p.112 | Listing 10-1 | DrawCString\nWrites Hello World with DrawCString.\nxxx", text);
        Assert.Equal(200, text.Length);
    }

    [Fact]
    public void Automatic_chunks_embed_as_before()
    {
        var doc = new ExtractedDocument("code", "csharp", "class Foo {}", null);
        Assert.Equal("src/Foo.cs (csharp)\nclass Foo {}", Chunker.EmbeddingText("src/Foo.cs", doc, new ChunkDraft("class Foo {}", null, 1, 1, null)));
    }
}

public class ConvertedPassageTests
{
    static Ragamuffin.Search.SearchHit Hit(string doc, long id, int ordinal, double score, double similarity, bool claude) =>
        new(Guid.Empty, doc, "c", "text", id, ordinal, null, null, null, null, false, null, score, similarity, true, "x") { ClaudeChunk = claude };

    [Fact]
    public void A_run_of_weak_neighbours_does_not_outrank_one_strong_complete_chunk()
    {
        // "Hello World on the graphics screen": the DrawCString reference entry (Claude's chunk) is the best match;
        // three adjacent opcode-table chunks only matched the keyword fallback.
        var hits = new[]
        {
            Hit("16 QuickDraw II.md", 1, 0, 0.0328, 0.62, claude: true),
            Hit("w65c816s.pdf", 356, 10, 0.0272, 0.36, claude: false),
            Hit("w65c816s.pdf", 357, 11, 0.0271, 0.37, claude: false),
            Hit("w65c816s.pdf", 358, 12, 0.0270, 0.32, claude: false),
        };
        Assert.Equal(["16 QuickDraw II.md", "w65c816s.pdf"], LeanText.Merge(hits).Select(p => p.DocumentName));
    }

    [Fact]
    public void A_converted_chunk_is_read_by_lines_and_cites_its_pdf_pages_title_and_id()
    {
        var p = new Passage("Toolbox.md", "c", "Drawing Text", 120, 160, 112, 113, false, "x", [])
        {
            ChunkIds = [48213], Title = "Listing 10-1", Kind = "listing",
        };
        Assert.Equal("lines 120-160", p.Location);
        Assert.Equal("lines=\"120-160\"", p.ReadHint);
        Assert.Equal("Toolbox.md · c · lines 120-160 · from pp. 112-113 · §Drawing Text · \"Listing 10-1\" · listing · chunk 48213", LeanText.Citation(p));
    }

    [Fact]
    public void Pdf_chunks_still_cite_pages()
    {
        var p = new Passage("book.pdf", "c", null, null, null, 12, 12, false, "x", []);
        Assert.Equal("page 12", p.Location);
        Assert.Equal("", p.SourcePages);
    }
}

public class PageReviewTests
{
    [Theory]
    [InlineData(PageSource.Embedded, "Plenty of embedded text on this page.", null, false)]
    [InlineData(PageSource.Embedded, "Plenty of embedded text on this page.", "garbled", true)]
    [InlineData(PageSource.Ocr, "Plenty of OCR text on this page here.", null, true)]
    [InlineData(PageSource.Embedded, "", null, true)]
    [InlineData(PageSource.Claude, "Plenty of text Claude checked here.", "garbled", false)]
    public void Pages_need_review_until_claude_has_checked_them(string source, string text, string? suspect, bool review)
    {
        var page = new DocumentPage { Source = source, EmbeddedText = text, OcrText = text, ClaudeText = text, SuspectReason = suspect };
        Assert.Equal(review, PageTools.NeedsReview(page));
    }
}

public class PageIssueTests
{
    static DocumentPage Page(int n, string source = PageSource.Ocr, string? issue = null) =>
        new() { Page = n, Source = source, OcrText = "Plenty of OCR text on this page here.", ClaudeText = "Plenty of text Claude checked.", Issue = issue };

    [Fact]
    public void A_page_with_a_recorded_problem_needs_review_until_claude_checks_it()
    {
        Assert.True(PageTools.NeedsReview(Page(1, PageSource.Layout, "Layout analysis failed")));
        Assert.True(PdfPages.HasIssue(Page(1, issue: "glm-ocr failed on 2 regions")));
        Assert.False(PdfPages.HasIssue(Page(1, PageSource.Claude, "glm-ocr failed on 2 regions")));
        Assert.False(PageTools.NeedsReview(Page(1, PageSource.Claude, "glm-ocr failed on 2 regions")));
    }

    [Fact]
    public void The_problems_scope_re_runs_only_unchecked_problem_pages()
    {
        var pages = new[] { Page(1), Page(2, issue: "x"), Page(3, PageSource.Claude, "x"), Page(4, issue: "y") };
        Assert.Equal([2, 4], PdfPages.SelectForOcr(pages, OcrScope.Problems));
        Assert.Equal(OcrScope.Problems, OcrScopes.Parse("problems"));
        Assert.Equal("problems", OcrScopes.Name(OcrScope.Problems));
    }

    [Fact]
    public void The_notice_is_rebuilt_from_page_problems()
    {
        Assert.Null(IngestWorker.IssueNotice([Page(1), Page(2, PageSource.Claude, "x")]));
        var notice = IngestWorker.IssueNotice([Page(1), Page(11, issue: "x"), Page(25, issue: "y")]);
        Assert.StartsWith("2 pages had OCR or layout problems (11, 25).", notice);
        Assert.Contains("list_pages(which=\"problems\")", notice);
    }
}

public class LocalFilesTests
{
    [Fact]
    public async Task Reads_only_inside_the_allowed_folders()
    {
        var root = Directory.CreateTempSubdirectory("rag-roots-").FullName;
        var outside = Path.Combine(Path.GetTempPath(), $"rag-outside-{Guid.NewGuid():N}.md");
        try
        {
            var inside = Path.Combine(root, "manual.md");
            await File.WriteAllTextAsync(inside, "# Hello");
            await File.WriteAllTextAsync(outside, "# Secret");
            var files = new LocalFiles(Options.Create(new RagOptions { Mcp = new McpOptions { ImportRoots = [root] } }));

            Assert.Equal("# Hello", System.Text.Encoding.UTF8.GetString((await files.ReadAsync(inside, default)).Bytes!));
            Assert.Contains("outside the folders", (await files.ReadAsync(outside, default)).Problem);
            Assert.Contains("outside the folders", (await files.ReadAsync(Path.Combine(root, "..", Path.GetFileName(outside)), default)).Problem);
            Assert.Contains("absolute path", (await files.ReadAsync("manual.md", default)).Problem);
            Assert.Contains("No file", (await files.ReadAsync(Path.Combine(root, "missing.md"), default)).Problem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }
}

public class HexDumpTests
{
    [Fact]
    public void Formats_offset_hex_and_ascii_like_xxd()
    {
        byte[] bytes = [0xa9, 0x00, 0x8d, 0x30, 0xc0, 0xa2, 0xff, 0x9a, 0x20, 0x58, 0xfc, 0x4c, 0x00, 0x08, 0x00, 0x00, (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O'];
        var lines = HexDump.Format(bytes).Split('\n');
        Assert.Equal("00000000: a9 00 8d 30 c0 a2 ff 9a  20 58 fc 4c 00 08 00 00  |...0.... X.L....|", lines[0]);
        Assert.StartsWith("00000010: 48 45 4c 4c 4f ", lines[1]);
        Assert.EndsWith("|HELLO|", lines[1]);
        // A short last line keeps the ASCII column aligned with full lines.
        Assert.Equal(lines[0].IndexOf('|'), lines[1].IndexOf('|'));
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Every_line_has_the_same_layout_so_line_numbers_map_to_offsets()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var lines = HexDump.Format(bytes).Split('\n');
        Assert.Equal(63, lines.Length);
        Assert.StartsWith("000003e0:", lines[62]);
        Assert.Equal(0x3e0, HexDump.OffsetOfLine(63));
    }

    [Fact]
    public void Empty_file_is_empty_text()
    {
        Assert.Equal("", HexDump.Format([]));
    }
}

public class MutedCollectionTests
{
    static CollectionSummary C(string name, bool muted = false) =>
        new(name, 10, 0, null, [], new Dictionary<string, int> { ["pdf"] = 10 }, [], muted);

    [Fact]
    public void Instructions_keep_muted_collections_but_say_they_are_searched_only_when_named()
    {
        var text = LibraryGuide.BuildInstructions([C("System 6 Docs"), C("System 5 Docs", muted: true)], 2000);
        Assert.Contains("- System 6 Docs (10 docs)", text);
        Assert.Contains("- System 5 Docs (10 docs, MUTED: searched only when named)", text);
        Assert.Contains("left out of search unless you name them", text);
    }

    [Fact]
    public void List_collections_marks_muted_ones()
    {
        var text = LibraryGuide.BuildList([C("System 5 Docs", muted: true)], 2000, titles: 0);
        Assert.StartsWith("## System 5 Docs (MUTED: left out of search unless named", text);
    }

    [Fact]
    public void A_named_collection_scope_is_explicit()
    {
        Assert.Equal(["System 5 Docs"], Ragamuffin.Search.SearchScope.For(" System 5 Docs ").Collections);
        Assert.Same(Ragamuffin.Search.SearchScope.Default, Ragamuffin.Search.SearchScope.For(null));
    }
}

public class StatsClaudeTests
{
    [Fact]
    public void Pages_claude_checked_count_toward_the_total_and_the_origin()
    {
        var all = new Ragamuffin.Stats.PageMix(0, 0, 0, 0, 0, Claude: 12);
        Assert.Equal(12, all.Total);
        Assert.Equal(Ragamuffin.Stats.TextOrigin.PdfClaude, Ragamuffin.Stats.StatsService.Classify("pdf", all));
        Assert.Equal(Ragamuffin.Stats.TextOrigin.PdfMixed, Ragamuffin.Stats.StatsService.Classify("pdf", new(10, 0, 0, 0, 0, Claude: 2)));
        Assert.Equal(Ragamuffin.Stats.TextOrigin.PdfMixed, Ragamuffin.Stats.StatsService.Classify("pdf", new(0, 0, 5, 0, 0, Claude: 2)));
        Assert.Equal(Ragamuffin.Stats.TextOrigin.PdfOcr, Ragamuffin.Stats.StatsService.Classify("pdf", new(0, 0, 5, 0, 3)));
        Assert.Equal(Ragamuffin.Stats.TextOrigin.PdfNoText, Ragamuffin.Stats.StatsService.Classify("pdf", new(0, 0, 0, 0, 3)));
    }
}
