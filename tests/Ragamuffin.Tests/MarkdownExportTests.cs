using Ragamuffin.Data;
using Ragamuffin.Ingestion;

namespace Ragamuffin.Tests;

public class MarkdownExportTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    static DocumentPage Page(int n, string? text, string source = PageSource.Layout) =>
        source == PageSource.Embedded
            ? new DocumentPage { Page = n, EmbeddedText = text, Source = source }
            : new DocumentPage { Page = n, LayoutText = text, Source = source };

    static MarkdownExport Build(params DocumentPage[] pages) => MarkdownExports.Build("Manuals/DOS.pdf", pages, 1200, 1800, Now);

    [Fact]
    public void Pages_get_markers_and_missing_text_is_noted()
    {
        var export = Build(
            Page(1, "# Chapter 1\n\nThe disk is divided into tracks and sectors."),
            Page(2, null),
            Page(3, "#$A9 is LDA immediate, as printed on this page.", PageSource.Embedded));
        var text = export.Text.Replace("\r\n", "\n");

        Assert.Contains("<!-- page 1 -->\n\n# Chapter 1", text);
        Assert.Contains("<!-- page 2: No text on this page -->", text);
        Assert.Contains("<!-- page 3 -->\n\n\\#$A9 is LDA", text); // plain text isn't read as a heading
        Assert.Equal(3, export.Pages);
        Assert.Equal(2, export.PagesWithText);
        Assert.Equal([2], export.Problems.Select(p => p.Page));
    }

    [Fact]
    public void Problems_are_listed_and_marked_but_their_text_is_kept()
    {
        var garbled = Page(4, "Tbe d1sk 1s d!v!ded 1nto tr@cks and secf0rs by tbe RWTS r0utines", PageSource.Embedded);
        garbled.SuspectReason = PageHealth.GarbledReason;
        var muted = Page(5, "Partly read", PageSource.Ocr);
        muted.OcrText = "Partly read";
        PageHealth.SetIssue(muted, "glm-ocr failed on 1 region");
        muted.IssueMuted = true;

        var export = Build(garbled, muted);

        Assert.Contains("Tbe d1sk 1s d!v!ded", export.Text);
        Assert.Contains("<!-- page 4: The text looks garbled (like OCR noise) -->", export.Text);
        Assert.Equal([(4, false), (5, true)], export.Problems.Select(p => (p.Page, p.Muted)));
    }

    [Fact]
    public void Chunks_follow_pages_and_carry_the_heading_breadcrumb()
    {
        var long1 = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Paragraph {i}: " + new string('x', 400)));
        var export = Build(
            Page(1, "# DOS Internals\n\n## The VTOC\n\n" + long1),
            Page(2, "The VTOC continues here.\n\n## The Catalog\n\nCatalog sectors are linked."));

        var plan = ChunkPlan.Validate(ChunkPlan.SplitLines(export.Text), export.Chunks, 60000);
        Assert.True(plan.Ok, string.Join("; ", plan.Errors));
        Assert.Empty(plan.Gaps);
        Assert.All(plan.Chunks, c => Assert.True(c.Content.Length <= 1800));
        Assert.True(plan.Chunks.Count(c => c.Spec.Page == 1) > 1);
        Assert.Equal("DOS Internals > The VTOC", plan.Chunks.First(c => c.Spec.Page == 2).Spec.Section);
        Assert.Equal("DOS Internals > The Catalog", plan.Chunks.Last().Spec.Section);
        Assert.StartsWith("## The Catalog", plan.Chunks.Last().Content);
    }

    [Fact]
    public void An_unclosed_code_fence_is_closed_at_the_end_of_its_page()
    {
        var export = Build(Page(1, "```\nLDA #$00\nSTA $C000"), Page(2, "# Next chapter\n\nThe next chapter starts on this page."));
        var text = export.Text.Replace("\r\n", "\n");
        Assert.Contains("STA $C000\n```\n\n<!-- page 2 -->", text);
    }

    [Theory]
    [InlineData("Manuals/Beneath Apple DOS.pdf", "Manuals/Beneath Apple DOS.md")]
    [InlineData("notes.PDF", "notes.md")]
    public void Default_name_swaps_the_extension(string pdf, string md) => Assert.Equal(md, MarkdownExports.DefaultName(pdf));
}
