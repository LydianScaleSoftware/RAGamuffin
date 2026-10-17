using Ragamuffin.Ingestion;

namespace Ragamuffin.Tests;

public class ChunkerTests
{
    static readonly Chunker Default = new(new ChunkerSettings(TargetChars: 200, MaxChars: 300, OverlapChars: 40));

    static ExtractedDocument Md(string text) => new("markdown", null, text, null);
    static ExtractedDocument Code(string text) => new("code", "csharp", text, null);

    [Fact]
    public void Markdown_records_heading_breadcrumbs()
    {
        var text = """
            # Guide

            Intro paragraph.

            ## Install

            """ + string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Install step {i} with enough words to fill the chunk up nicely."));

        var chunks = Default.Chunk(Md(text));

        Assert.Contains(chunks, c => c.HeadingPath!.StartsWith("Guide") && c.Content.Contains("Intro paragraph."));
        Assert.Contains(chunks, c => c.HeadingPath == "Guide > Install" && c.Content.Contains("Install step 6"));
    }

    [Fact]
    public void Markdown_heading_reset_drops_deeper_levels()
    {
        var text = "# A\n\n## B\n\nbody b " + new string('x', 250) + "\n\n# C\n\nbody c " + new string('y', 180);
        var chunks = Default.Chunk(Md(text));
        Assert.Equal("C", chunks[^1].HeadingPath);
        Assert.StartsWith("# C", chunks[^1].Content);
        Assert.Contains(chunks, c => c.HeadingPath == "A > B" && c.Content.StartsWith("# A\n\n## B\nbody b"));
    }

    [Fact]
    public void Tiny_sections_merge_under_their_common_ancestor()
    {
        var chunks = Default.Chunk(Md("# Top\n\n## One\n\nshort\n\n## Two\n\nalso short"));
        var chunk = Assert.Single(chunks);
        Assert.Equal("Top", chunk.HeadingPath);
    }

    [Fact]
    public void Markdown_fenced_code_is_not_split_on_blank_lines()
    {
        var text = "# Code\n\n```\nline one\n\nline two\n```\n";
        var chunks = Default.Chunk(Md(text));
        Assert.Single(chunks);
        Assert.Contains("line one\n\nline two", chunks[0].Content);
    }

    [Fact]
    public void Code_chunks_have_line_ranges_and_respect_max_size()
    {
        var lines = Enumerable.Range(1, 60).Select(i => i % 5 == 0 ? "" : $"    var value{i} = Compute({i});");
        var chunks = Default.Chunk(Code(string.Join("\n", lines)));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 300, $"chunk of {c.Content.Length} chars"));
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(59, chunks[^1].EndLine);
        for (var i = 1; i < chunks.Count; i++)
            Assert.True(chunks[i].StartLine <= chunks[i - 1].EndLine + 2, "chunks should be contiguous or overlap");
    }

    [Fact]
    public void Consecutive_chunks_overlap()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i:D2} of the file");
        var chunks = Default.Chunk(Code(string.Join("\n", lines)));

        Assert.True(chunks.Count > 1);
        var lastLineOfFirst = chunks[0].Content.Split('\n')[^1];
        Assert.Contains(lastLineOfFirst, chunks[1].Content);
        Assert.True(chunks[1].StartLine <= chunks[0].EndLine);
    }

    [Fact]
    public void Very_long_line_is_split()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 300));
        var chunks = Default.Chunk(new ExtractedDocument("text", null, text, null));
        Assert.True(chunks.Count >= 5);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 300));
        Assert.All(chunks, c => Assert.Equal(1, c.StartLine));
    }

    [Fact]
    public void Pdf_chunks_carry_page_numbers_and_no_lines()
    {
        var doc = new ExtractedDocument("pdf", null, "", [new PageText(1, "first page text"), new PageText(3, "third page text")]);
        var chunks = Default.Chunk(doc);
        Assert.Equal([1, 3], chunks.Select(c => c.Page!.Value));
        Assert.All(chunks, c => Assert.Null(c.StartLine));
    }

    [Fact]
    public void Empty_text_yields_no_chunks()
    {
        Assert.Empty(Default.Chunk(Md("\n\n   \n")));
    }

    [Fact]
    public void Embedding_text_includes_context_header()
    {
        var doc = Code("x");
        var text = Chunker.EmbeddingText("src/Foo.cs", doc, new ChunkDraft("class Foo {}", null, 1, 1, null));
        Assert.Equal("src/Foo.cs (csharp)\nclass Foo {}", text);
    }
}
