using Ragamuffin.Mcp;
using Ragamuffin.Search;

namespace Ragamuffin.Tests;

public class LeanTextTests
{
    static readonly Guid DocA = Guid.NewGuid();
    static readonly Guid DocB = Guid.NewGuid();

    static SearchHit Hit(Guid doc, string name, long chunk, int ordinal, string content, int? page = null, int? start = null, int? end = null, double score = 0.03) =>
        new(doc, name, "orca/goldengate", "text", chunk, ordinal, "Graphics", start, end, page, false, null, score, 0.8, false, content);

    static Passage P(string name, string text) => new(name, "c", null, 1, 10, null, null, false, text, []);

    [Fact]
    public void Query_terms_drop_stop_words_and_keep_identifiers()
    {
        Assert.Equal(["orca/c", "startgraph", "quickdraw", "$a604"], LeanText.QueryTerms("How does an ORCA/C program use startgraph with QuickDraw and $A604?"));
    }

    [Fact]
    public void Overlapping_neighbours_join_without_repeating_text()
    {
        var first = "Line one of the section.\nLine two continues here and ends the chunk with some shared overlap text.";
        var second = "ends the chunk with some shared overlap text.\nLine three is new.";
        Assert.Equal(first + "\nLine three is new.", LeanText.JoinOverlapping(first, second));
        Assert.Equal("a\nb", LeanText.JoinOverlapping("a", "b"));
    }

    [Fact]
    public void Consecutive_chunks_of_one_document_merge_and_keep_the_better_rank()
    {
        var hits = new[]
        {
            Hit(DocA, "manual.txt", 2, 5, "five", start: 50, end: 60),
            Hit(DocB, "other.txt", 9, 1, "other"),
            Hit(DocA, "manual.txt", 1, 4, "four", start: 40, end: 52),
        };
        var passages = LeanText.Merge(hits);
        Assert.Equal(2, passages.Count);
        Assert.Equal("manual.txt", passages[0].DocumentName);
        Assert.Equal("four\nfive", passages[0].Text);
        Assert.Equal("lines 40-60", passages[0].Location);
    }

    [Fact]
    public void An_explanation_and_its_listing_outrank_a_single_better_chunk()
    {
        // "Hello World in a graphics program": the Drawing Text explanation and Listing 10-1 each rank mid-list, but
        // together they are the answer and must not fall behind single chunks that merely mention "Hello, world".
        var hits = new[]
        {
            Hit(DocB, "ORCA_M.txt", 9, 40, "the words Hello world! should be written to the screen", score: 0.03),
            Hit(DocA, "Toolbox C.txt", 1, 560, "Drawing Text\nHere's a short program that writes \"Hello, world.\"", score: 0.016),
            Hit(DocA, "Toolbox C.txt", 2, 561, "DrawCString(\"Hello, world.\");\nListing 10-1", score: 0.015),
        };
        var passages = LeanText.Merge(hits);
        Assert.Equal(["Toolbox C.txt", "ORCA_M.txt"], passages.Select(p => p.DocumentName));
    }

    [Fact]
    public void Of_one_lesson_with_different_code_the_document_the_question_names_is_kept()
    {
        var prose = string.Join(' ', Enumerable.Repeat("To actually write text to the screen, all you do is pass a string to QuickDraw.", 4));
        var pascal = P("Toolbox Pascal.txt", prose + "\nbegin\nStartGraph(320);\nPenNormal;\nDrawString(@\"Hello, world.\");\nreadln;\nEndGraph;\nend.");
        var c = P("Toolbox C.txt", prose + "\n#include <orca.h>\n{\nstartgraph(320);\nPenNormal();\nDrawCString(\"Hello, world.\");\nendgraph();\n}");
        var kept = Assert.Single(LeanText.Dedupe([pascal, c], "How do I write Hello World in a graphics program with ORCA/C?"));
        Assert.Equal("Toolbox C.txt", kept.DocumentName);
        Assert.Equal(["Toolbox Pascal.txt"], kept.AlsoIn);
        Assert.Equal("Toolbox Pascal.txt", Assert.Single(LeanText.Dedupe([pascal, c], "Hello World in ORCA/Pascal")).DocumentName);
        Assert.Equal("Toolbox C.txt", Assert.Single(LeanText.Dedupe([c, P("Toolbox Pascal 2.0.txt", c.Text)], "Pascal")).DocumentName);
    }

    [Fact]
    public void Duplicate_passages_from_another_edition_are_dropped_and_noted()
    {
        var text = string.Join(' ', Enumerable.Repeat("the easiest way to start QuickDraw II is the library function startgraph", 4));
        var kept = LeanText.Dedupe([P("ORCA_C_2.2_Manual.txt", text), P("GS-06 ORCA:C 2.0.txt", text + " extra words"), P("other.txt", "something else entirely different here")]);
        Assert.Equal(["ORCA_C_2.2_Manual.txt", "other.txt"], kept.Select(p => p.DocumentName));
        Assert.Equal(["GS-06 ORCA:C 2.0.txt"], kept[0].AlsoIn);
    }

    [Fact]
    public void Excerpt_keeps_the_lines_with_the_query_terms()
    {
        var lines = Enumerable.Range(1, 40).Select(i => i == 25 ? "startgraph(320); MoveTo(10, 40);" : $"filler line number {i} with nothing useful").ToList();
        var excerpt = LeanText.Excerpt(string.Join('\n', lines), ["startgraph", "moveto"], 200);
        Assert.Contains("startgraph(320); MoveTo(10, 40);", excerpt);
        Assert.StartsWith("…", excerpt);
        Assert.True(excerpt.Length <= 202);
    }

    [Fact]
    public void Excerpt_picks_separate_parts_that_cover_different_terms()
    {
        var lines = Enumerable.Range(1, 60).Select(i => $"filler line {i} about the screen and quickdraw tools").ToList();
        lines[10] = "After startgraph, text written with printf is drawn by QuickDraw; use MoveTo to place the text.";
        lines[50] = "void startgraph(int width); starts QuickDraw II.";
        var excerpt = LeanText.Excerpt(string.Join('\n', lines), LeanText.QueryTerms("start QuickDraw and draw text on the screen with startgraph"), 400);
        Assert.Contains("text written with printf", excerpt);
        Assert.Contains("void startgraph(int width)", excerpt);
        Assert.Contains("\n…\n", excerpt);
        Assert.True(excerpt.Length <= 410, $"{excerpt.Length} chars");
    }

    [Fact]
    public void Dedent_removes_shared_indentation_but_keeps_code_structure()
    {
        Assert.Equal("int main (void)\n{\n    startgraph(320);\n}", LeanText.Dedent("    int main (void)\n    {\n        startgraph(320);\n    }"));
        Assert.Equal("no indent\n  some", LeanText.Dedent("no indent\n  some"));
    }

    [Fact]
    public void Merged_passages_are_capped_at_three_chunks()
    {
        var hits = Enumerable.Range(0, 6).Select(i => Hit(DocA, "manual.txt", i + 1, i, $"chunk {i}", start: i * 10 + 1, end: i * 10 + 12)).ToArray();
        var passages = LeanText.Merge(hits);
        Assert.Equal(2, passages.Count);
        Assert.All(passages, p => Assert.True(p.Text.Split('\n').Length <= 3));
    }

    [Fact]
    public void Short_text_is_not_excerpted()
    {
        Assert.Equal("short", LeanText.Excerpt("  short  ", ["x"], 100));
    }

    [Fact]
    public void Search_output_fits_the_budget_and_shows_how_to_read_more()
    {
        var big = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}: QuickDraw startgraph detail number {i}"));
        var passages = Enumerable.Range(0, 5).Select(i => new Passage($"doc{i}.txt", "c", "Graphics", 100 * i + 1, 100 * i + 200, null, null, false, big, [])).ToList();
        var text = LeanText.FormatSearch("startgraph quickdraw", passages, 1500);
        Assert.True(LeanText.Tokens(text) <= 1500 * 1.05, $"{LeanText.Tokens(text)} tokens");
        Assert.Contains("get_document(\"doc0.txt\", lines=\"1-200\")", text);
        Assert.Contains("[5] doc4.txt · c · lines 401-600 · §Graphics", text);
    }

    [Fact]
    public void Pdf_passages_cite_pages_and_ocr()
    {
        var p = new Passage("book.pdf", "c", null, null, null, 12, 13, true, "x", ["copy.pdf"]);
        Assert.Equal("pages 12-13", p.Location);
        Assert.Equal("pages=\"12-13\"", p.ReadHint);
        Assert.Equal("book.pdf · c · pages 12-13 (OCR) · also in: copy.pdf", LeanText.Citation(p));
    }

    [Fact]
    public void No_results_suggests_what_to_try()
    {
        Assert.Contains("No matching passages", LeanText.FormatSearch("zzz", [], 1500));
    }
}

public class DocumentReaderTests
{
    [Theory]
    [InlineData("12", 12, 12)]
    [InlineData("12-14", 12, 14)]
    [InlineData(" 12 – 14 ", 12, 14)]
    [InlineData("12 to 14", 12, 14)]
    [InlineData("14-12", 12, 14)]
    [InlineData("0", 1, 1)]
    public void Parses_ranges(string text, int start, int end)
    {
        Assert.Equal(new TextRange(start, end), DocumentReader.ParseRange(text));
    }

    [Fact]
    public void Open_ended_range_reads_to_the_end()
    {
        Assert.Equal(new TextRange(40, null), DocumentReader.ParseRange("40-"));
    }

    [Theory]
    [InlineData("pages 1-3")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects_unreadable_ranges(string text)
    {
        Assert.Null(DocumentReader.ParseRange(text));
    }

    static readonly string[] Names = ["Manuals/ORCA_C_2.2_Manual.txt", "Manuals/GS-20 Toolbox C.txt", "Manuals/GS-06 ORCA:C 2.0.txt", "notes.md"];

    [Theory]
    [InlineData("Manuals/ORCA_C_2.2_Manual.txt", "Manuals/ORCA_C_2.2_Manual.txt")]
    [InlineData("orca_c_2.2_manual.txt", "Manuals/ORCA_C_2.2_Manual.txt")]
    [InlineData("ORCA_C_2.2_Manual", "Manuals/ORCA_C_2.2_Manual.txt")]
    [InlineData("Toolbox C", "Manuals/GS-20 Toolbox C.txt")]
    [InlineData("notes", "notes.md")]
    public void Finds_documents_by_forgiving_names(string key, string expected)
    {
        Assert.Equal([expected], DocumentReader.MatchByName(Names, key));
    }

    [Fact]
    public void Ambiguous_partial_names_return_every_candidate()
    {
        Assert.Equal(2, DocumentReader.MatchByName(Names, "ORCA").Count);
        Assert.Empty(DocumentReader.MatchByName(Names, "nothing"));
    }

    [Fact]
    public void Detects_headings_in_plain_text_manuals()
    {
        string[] lines =
        [
            "Chapter 2 - Exploring the System",
            "    Some indented prose that explains things.",
            "    More prose.",
            "Stand-Alone Programs",
            "    Any program that uses the toolbox must start it.",
            "",
            "Drawing Text",
            "",
            "Listing 10-1:  Writing Text to the Graphics Screen",
            "",
            "startgraph(320);",
            "Contents\t1",
            "This line ends with a period.",
        ];
        var headings = DocumentReader.DetectHeadings(lines);
        Assert.Equal([(1, "Chapter 2 - Exploring the System", 1), (4, "Stand-Alone Programs", 2), (7, "Drawing Text", 2)],
            headings.Select(h => (h.Line, h.Text, h.Level)));
    }

    [Fact]
    public void Splits_pdf_text_into_pages()
    {
        var pages = DocumentReader.SplitPages("[Page 1]\nfirst\n\n[Page 3]\nthird\npage");
        Assert.Equal([1, 3], pages.Keys);
        Assert.Equal("third\npage", pages[3]);
    }
}

public class LibraryAnswererTests
{
    static Passage P(string name, string text, int? page = null) =>
        new(name, "c", "Graphics", page is null ? 1 : null, page is null ? 30 : null, page, page, false, text, []);

    [Fact]
    public void Context_numbers_passages_and_fits_the_budget()
    {
        var big = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"startgraph detail {i}"));
        var context = LibraryAnswerer.BuildContext("startgraph", [P("a.txt", big), P("b.pdf", big, page: 7)], 1000);
        Assert.StartsWith("[1] a.txt, lines 1-30 — Graphics", context);
        Assert.Contains("[2] b.pdf, page 7 — Graphics", context);
        Assert.True(LeanText.Tokens(context) <= 1100, $"{LeanText.Tokens(context)} tokens");
    }

    [Fact]
    public void Room_short_passages_leave_goes_to_a_long_listing()
    {
        // The spiral listing ends with endgraph(); an even split of the budget cut it off before that line.
        var listing = string.Join('\n', Enumerable.Range(1, 60).Select(i => $"   LineTo({i}, {i}); /* step {i} */")) + "\nendgraph();";
        var context = LibraryAnswerer.BuildContext("startgraph", [P("short1.txt", "startgraph starts QuickDraw."), P("listing.txt", "startgraph(640);\n" + listing), P("short2.txt", "Use startgraph.")], 800);
        Assert.Contains("endgraph();", context);
        Assert.True(LeanText.Tokens(context) <= 850, $"{LeanText.Tokens(context)} tokens");
    }

    [Fact]
    public void Answer_lists_sources_with_ready_to_use_reads()
    {
        var text = LibraryAnswerer.FormatAnswer("Use startgraph(320) [1].", [P("Toolbox C.txt", "x"), P("book.pdf", "y", page: 12)], "qwen3:8b");
        Assert.StartsWith("Use startgraph(320) [1].", text);
        Assert.Contains("[1] Toolbox C.txt (c) — get_document(\"Toolbox C.txt\", lines=\"1-30\")", text);
        Assert.Contains("[2] book.pdf (c) — get_document(\"book.pdf\", pages=\"12-12\")", text);
        Assert.Contains("written by qwen3:8b", text);
    }

    [Fact]
    public void Calls_missing_from_the_sources_are_flagged()
    {
        var passages = new[] { P("manual.txt", "startgraph(640);\nMoveTo(280, 40);\nprintf(\"Hello\");") };
        var answer = "```c\nint main(void) {\n    startgraph(640);\n    MoveTo(10, 10);\n    TextOut(10, 10, \"Hi\");\n    puts(\"x\");\n    if (x) return 0;\n}\n```";
        Assert.Equal(["TextOut"], LibraryAnswerer.UnsupportedCalls(answer, passages));
        Assert.Contains("WARNING: not found in the sources, may be invented: TextOut", LibraryAnswerer.FormatAnswer(answer, passages, "m", ["TextOut"]));
    }

    [Fact]
    public void Prose_before_parentheses_is_not_a_call()
    {
        var passages = new[] { P("manual.txt", "MoveTo(50, 50);\nDrawCString(\"Hello, world.\");") };
        var answer = "It moves the pen to the position (50, 50), then calls DrawCString(\"Hello, world.\") [1].";
        Assert.Empty(LibraryAnswerer.UnsupportedCalls(answer, passages));
    }
}
