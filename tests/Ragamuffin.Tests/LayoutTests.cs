using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Layout;
using Ragamuffin.Ocr;
using SkiaSharp;

namespace Ragamuffin.Tests;

public class LayoutAssemblerTests
{
    static WordBox W(string text, double x, double y, int order = 0, double w = 40, double h = 10) => new(text, x, y, x + w, y + h, order);

    [Theory]
    [InlineData("doc_title", "Big  Title", "# Big Title")]
    [InlineData("paragraph_title", "Section\n2.1", "## Section 2.1")]
    [InlineData("figure_title", "Figure 2-2. Diagram", "*Figure 2-2. Diagram*")]
    [InlineData("image", "A - Accumulator\nX - Index", "[Figure text: A - Accumulator / X - Index]")]
    [InlineData("text", "  Body text.\nSecond line.  ", "Body text.\nSecond line.")]
    public void Formats_regions_by_label(string label, string text, string expected)
    {
        Assert.Equal(expected, LayoutAssembler.FormatRegion(label, text));
    }

    [Fact]
    public void Empty_regions_are_dropped()
    {
        Assert.Null(LayoutAssembler.FormatRegion("text", "   "));
        Assert.Equal("a\n\nb", LayoutAssembler.Join(["a", null, "  ", "b"]));
    }

    [Fact]
    public void Short_headers_and_footers_are_furniture_but_long_ones_are_content()
    {
        Assert.True(LayoutAssembler.IsFurniture("footer", "The Western Design Center W65C816S 7"));
        Assert.True(LayoutAssembler.IsFurniture("number", "19"));
        Assert.False(LayoutAssembler.IsFurniture("footer", string.Join(' ', Enumerable.Repeat("word", 16))));
        Assert.False(LayoutAssembler.IsFurniture("text", "x"));
    }

    [Theory]
    [InlineData("table", "Table Recognition:")]
    [InlineData("formula", "Formula Recognition:")]
    [InlineData("text", "Text Recognition:")]
    [InlineData("image", "Text Recognition:")]
    public void Chooses_ocr_prompt_by_region(string label, string prompt)
    {
        Assert.Equal(prompt, LayoutAssembler.OcrPrompt(label));
    }

    [Fact]
    public void Html_table_becomes_markdown_with_colspan_and_escaped_pipes()
    {
        const string html = "<table><tr><td colspan=\"2\">Code</td><td>Part</td></tr><tr><td>0</td><td>$00</td><td>a|b</td></tr></table>";
        Assert.Equal("| Code |  | Part |\n| --- | --- | --- |\n| 0 | $00 | a\\|b |", LayoutAssembler.HtmlTableToMarkdown(html));
    }

    [Fact]
    public void Html_tables_inside_text_regions_become_markdown()
    {
        var text = LayoutAssembler.FormatRegion("text", "Values:\n\n<table border=\"1\"><tr><td>0</td><td>On</td></tr><tr><td>1</td><td>Off</td></tr></table>\n\nAfter.");
        Assert.Equal("Values:\n\n| 0 | On |\n| --- | --- |\n| 1 | Off |\n\nAfter.", text);
    }

    [Fact]
    public void Single_column_table_becomes_lines()
    {
        Assert.Equal("one\ntwo", LayoutAssembler.HtmlTableToMarkdown("<table><tr><td>one</td></tr><tr><td>two</td></tr></table>"));
    }

    [Fact]
    public void Table_html_region_is_formatted_as_markdown()
    {
        var text = LayoutAssembler.FormatRegion("table", "<html><body><table><tr><td>A</td><td>B</td></tr></table></body></html>");
        Assert.Equal("| A | B |\n| --- | --- |", text);
    }

    [Fact]
    public void Words_go_to_the_region_they_mostly_overlap()
    {
        double[][] regions = [[0, 0, 100, 50], [0, 60, 100, 120]];
        var words = new[] { W("top", 10, 10), W("bottom", 10, 80), W("outside", 300, 300), W("edge", 95, 20, w: 40) };
        var assigned = LayoutAssembler.AssignWords(regions, words, out var unplaced);
        // "edge" mostly sticks out past the first region's right side, like the last word on a line: it still belongs there.
        Assert.Equal(["top", "edge"], assigned[0].Select(w => w.Text));
        Assert.Equal(["bottom"], assigned[1].Select(w => w.Text));
        Assert.Equal(["outside"], unplaced.Select(w => w.Text));
    }

    [Fact]
    public void Words_are_grouped_into_lines_left_to_right()
    {
        var words = new[] { W("world", 60, 11), W("Hello", 10, 10), W("next", 10, 30), W("line", 60, 29) };
        Assert.Equal("Hello world\nnext line", LayoutAssembler.WordsToLines(words));
    }

    [Fact]
    public void Content_order_keeps_side_by_side_columns_apart()
    {
        // Two columns whose rows share the same heights: geometric lines would merge them; content order doesn't.
        var words = new[]
        {
            W("ADC", 10, 10, 1), W("61", 60, 10, 2),
            W("AND", 10, 30, 3), W("21", 60, 30, 4),
            W("LDA", 300, 10, 5), W("A9", 350, 10, 6),
        };
        Assert.Equal("ADC 61\nAND 21\nLDA A9", LayoutAssembler.ContentOrderLines(words));
        Assert.Equal("ADC 61 LDA A9\nAND 21", LayoutAssembler.WordsToLines(words));
    }

    [Fact]
    public void Table_is_rebuilt_from_cells_and_embedded_words()
    {
        const string html = "<table><tr><td>x</td><td>y</td></tr><tr><td>x</td><td>y</td></tr></table>";
        List<double[]> cells = [[0, 0, 50, 20], [50, 0, 100, 20], [0, 20, 50, 40], [50, 20, 100, 40]];
        var words = new[] { W("Value", 5, 5, w: 30), W("Meaning", 55, 5, w: 30), W("0", 5, 25, w: 10), W("Highlighted", 55, 25, w: 40) };
        Assert.Equal("| Value | Meaning |\n| --- | --- |\n| 0 | Highlighted |", LayoutAssembler.TableFromCells(html, cells, words));
    }

    [Fact]
    public void Table_rebuild_gives_up_when_cells_and_html_disagree_or_cells_are_coarse()
    {
        const string html = "<table><tr><td>x</td><td>y</td></tr></table>";
        Assert.Null(LayoutAssembler.TableFromCells(html, [[0, 0, 10, 10]], [W("a", 1, 1)]));

        var tallCell = Enumerable.Range(0, 5).Select(i => W($"row{i}", 5, i * 15, w: 20)).ToList();
        Assert.Null(LayoutAssembler.TableFromCells(html, [[0, 0, 50, 100], [50, 0, 100, 100]], tallCell));
    }

    [Fact]
    public void Regions_scale_with_their_table_cells()
    {
        var region = new LayoutRegion("table", [10, 20, 30, 40], "", new LayoutTable("<table/>", [[1, 2, 3, 4]]));
        var scaled = LayoutAssembler.Scale(region, 2);
        Assert.Equal([20d, 40, 60, 80], scaled.Bbox);
        Assert.Equal([2d, 4, 6, 8], scaled.Table!.Cells[0]);
    }
}

public class LayoutGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 270)]
    [InlineData(180, 180)]
    [InlineData(270, 90)]
    public void Upright_rotation_undoes_the_reported_angle(int angle, int clockwise)
    {
        Assert.Equal(clockwise, PageAnalyzer.UprightRotation(angle));
    }

    [Fact]
    public void Rotating_clockwise_moves_the_top_left_corner_to_the_top_right()
    {
        using var source = new SKBitmap(4, 2);
        using (var c = new SKCanvas(source)) c.Clear(SKColors.White);
        source.SetPixel(0, 0, SKColors.Black);
        using var rotated = OcrEngine.Rotate(source, 90);
        Assert.Equal((2, 4), (rotated.Width, rotated.Height));
        Assert.Equal(SKColors.Black, rotated.GetPixel(1, 0));
    }

    [Fact]
    public void Downscale_never_enlarges()
    {
        using var big = new SKBitmap(4000, 2000);
        using var small = new SKBitmap(800, 600);
        using var a = OcrEngine.Downscale(big, 2000);
        using var b = OcrEngine.Downscale(small, 2000);
        Assert.Equal((2000, 1000), (a.Width, a.Height));
        Assert.Equal((800, 600), (b.Width, b.Height));
    }
}

public class LayoutPagesTests
{
    static DocumentPage Page(int n, string? embedded, string source = PageSource.Embedded, DateTimeOffset? layoutAt = null, string? ocr = null) =>
        new() { Page = n, EmbeddedText = embedded, Source = source, LayoutAt = layoutAt, OcrText = ocr, LayoutText = layoutAt is null ? null : embedded };

    const string Words = "Enough embedded words on this page to count as text.";

    [Fact]
    public void Selects_pages_for_layout()
    {
        var pages = new[]
        {
            Page(1, Words),
            Page(2, Words, PageSource.Layout, DateTimeOffset.UtcNow),
            Page(3, ""),
            Page(4, Words, PageSource.Ocr, ocr: Words),
        };
        Assert.Equal([1], PdfPages.SelectForLayout(pages, LayoutMode.New));
        Assert.Equal([1, 2], PdfPages.SelectForLayout(pages, LayoutMode.Redo));
    }

    [Fact]
    public void Embedded_source_prefers_layout_text()
    {
        Assert.Equal(PageSource.Layout, Page(1, Words, layoutAt: DateTimeOffset.UtcNow).EmbeddedSource);
        Assert.Equal(PageSource.Embedded, Page(1, Words).EmbeddedSource);
    }

    [Fact]
    public void Layout_and_ocr_pages_are_markdown_but_raw_embedded_pages_are_not()
    {
        var doc = PdfPages.ToExtracted([
            Page(1, Words),
            Page(2, Words, PageSource.Layout, DateTimeOffset.UtcNow),
            Page(3, "", PageSource.Ocr, ocr: "## Heading from OCR text here"),
        ]);
        Assert.Equal([false, true, true], doc.Pages!.Select(p => p.Markdown));
    }

    [Fact]
    public void Heading_breadcrumb_carries_across_pdf_pages()
    {
        var chunker = new Chunker(new ChunkerSettings(200, 300, 40));
        var doc = new ExtractedDocument("pdf", null, "", [
            new PageText(1, "## Chapter 2\n\nIntro text on the first page.", Markdown: true),
            new PageText(2, "Continued text on the second page.", Markdown: true),
            new PageText(3, "## Apple IIe Emulation\n\nNew section text.", Markdown: true),
        ]);
        var chunks = chunker.Chunk(doc);
        Assert.Equal(["Chapter 2", "Chapter 2", "Apple IIe Emulation"], chunks.Select(c => c.HeadingPath));
        Assert.Equal([1, 2, 3], chunks.Select(c => c.Page!.Value));
    }

    [Fact]
    public void Raw_embedded_pages_ignore_hash_lines()
    {
        var chunker = new Chunker(new ChunkerSettings(200, 300, 40));
        var doc = new ExtractedDocument("pdf", null, "", [new PageText(1, "# of bytes\nLDA #Imm A9", Markdown: false)]);
        Assert.Null(Assert.Single(chunker.Chunk(doc)).HeadingPath);
    }
}
