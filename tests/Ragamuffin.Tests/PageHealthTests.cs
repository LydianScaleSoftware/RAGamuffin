using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Mcp;
using Ragamuffin.Ocr;
using SkiaSharp;

namespace Ragamuffin.Tests;

public class PageHealthTests
{
    const string Words = "The coolant pump on unit B7 must be replaced every 4000 operating hours, without fail.";

    static DocumentPage Page(int n = 1, string? text = Words, string source = PageSource.Embedded) =>
        new() { Page = n, EmbeddedText = source == PageSource.Embedded ? text : Words, OcrText = source == PageSource.Ocr ? text : null, Source = source };

    [Fact]
    public void Page_with_text_and_nothing_wrong_is_unverified() =>
        Assert.Equal(PageState.Unverified, PageHealth.StateOf(Page()));

    [Fact]
    public void Recorded_problem_counts_until_muted()
    {
        var p = Page();
        PageHealth.SetIssue(p, "glm-ocr failed on 1 region.");
        Assert.Equal(PageState.Problem, PageHealth.StateOf(p));
        p.IssueMuted = true;
        Assert.Equal(PageState.Muted, PageHealth.StateOf(p));
    }

    [Fact]
    public void A_new_issue_is_never_muted()
    {
        var p = Page();
        PageHealth.SetIssue(p, "first");
        p.IssueMuted = true;
        PageHealth.SetIssue(p, "second");
        Assert.False(p.IssueMuted);
        PageHealth.SetIssue(p, "second");
        Assert.False(p.IssueMuted);
    }

    [Fact]
    public void Note_is_not_a_problem()
    {
        var p = Page();
        PageHealth.SetIssue(p, "Layout analysis skipped because the page is rotated.", IssueKinds.Note);
        Assert.Equal(PageState.Note, PageHealth.StateOf(p));
        Assert.Null(PageHealth.ProblemOf(p));
        Assert.False(PdfPages.HasIssue(p));
    }

    [Fact]
    public void Page_without_text_is_a_problem_unless_its_image_is_blank()
    {
        var p = Page(text: "");
        Assert.Equal(PageState.Problem, PageHealth.StateOf(p));
        p.Blank = true;
        Assert.Equal(PageState.Blank, PageHealth.StateOf(p));
        p.Blank = false;
        Assert.Contains("content", PageHealth.ProblemOf(p));
    }

    [Fact]
    public void Checked_pages_are_verified_and_their_old_issue_no_longer_counts()
    {
        var p = Page();
        PageHealth.SetIssue(p, "glm-ocr failed on 1 region.");
        p.ClaudeText = Words;
        p.Source = PageSource.Claude;
        Assert.Equal(PageState.Verified, PageHealth.StateOf(p));
        Assert.False(PdfPages.HasIssue(p));
    }

    [Fact]
    public void A_users_mark_lapses_when_the_text_changes()
    {
        var p = Page();
        p.VerifiedAt = DateTimeOffset.UtcNow;
        Assert.Equal(PageState.Verified, PageHealth.StateOf(p));
        PageHealth.TextChanged(p);
        Assert.Equal(PageState.Unverified, PageHealth.StateOf(p));
    }

    [Fact]
    public void Garbled_embedded_text_is_a_problem_but_an_ocr_layer_is_not()
    {
        var garbled = Page();
        garbled.SuspectReason = PageHealth.GarbledReason;
        Assert.Equal(PageState.Problem, PageHealth.StateOf(garbled));
        var layer = Page();
        layer.SuspectReason = "Invisible text over a scanned image (typical of OCR'd scans)";
        Assert.Equal(PageState.Unverified, PageHealth.StateOf(layer));
    }

    [Fact]
    public void Garbled_text_wins_over_how_the_pdf_was_made()
    {
        var noise = string.Join(' ', Enumerable.Repeat("th~e w0r|d inf;ormation", 15));
        Assert.Equal(PageHealth.GarbledReason, PdfPages.SuspectReason(invisibleShare: 0.95, imageCover: 1, null, noise));
    }

    [Fact]
    public void Counts_and_summary_follow_the_states()
    {
        var pages = new List<DocumentPage> { Page(1), Page(2, ""), Page(3), Page(4) };
        pages[1].Blank = true;
        PageHealth.SetIssue(pages[2], "glm-ocr failed on 1 region.");
        pages[3].VerifiedAt = DateTimeOffset.UtcNow;
        var doc = new Document { Name = "x.pdf", Collection = "default", ContentType = "pdf", Sha256 = "", PageCount = 4 };
        PageHealth.Apply(doc, pages);
        Assert.Equal((1, 1, 1), (doc.ProblemPages, doc.BlankPages, doc.VerifiedPages));
        Assert.Equal("1 problem page", PageHealth.Summary(doc));
        pages[2].IssueMuted = true;
        PageHealth.Apply(doc, pages);
        Assert.Equal(1, doc.MutedPages);
        Assert.StartsWith("no problems", PageHealth.Summary(doc));
        doc.VerifiedAt = DateTimeOffset.UtcNow;
        doc.VerifiedBy = "user";
        Assert.StartsWith("verified", PageHealth.Summary(doc));
    }

    [Fact]
    public void Blank_detection_ignores_dark_margins_but_sees_content()
    {
        using var blank = new SKBitmap(340, 440);
        using (var c = new SKCanvas(blank))
        {
            c.Clear(new SKColor(235, 228, 210));                      // yellowed paper
            using var dark = new SKPaint { Color = SKColors.Black };
            c.DrawRect(0, 0, 20, 440, dark);                          // binding along the edge
        }
        Assert.True(BlankPage.IsBlank(blank));

        using var figure = blank.Copy();
        using (var c = new SKCanvas(figure))
        using (var ink = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, Style = SKPaintStyle.Stroke })
            c.DrawRect(80, 120, 180, 160, ink);
        Assert.False(BlankPage.IsBlank(figure));
    }

    [Fact]
    public void Sample_spreads_through_the_document_and_skips_settled_pages()
    {
        var pages = Enumerable.Range(1, 200).Select(n => Page(n)).ToList();
        pages[0].Source = PageSource.Claude;
        pages[1].Blank = true; pages[1].EmbeddedText = "";
        pages[150].EmbeddedText = "| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";
        var sample = PageTools.Sample(pages);
        Assert.Equal(12, sample.Count);
        Assert.DoesNotContain(sample, p => p.Page is 1 or 2);
        Assert.Contains(sample, p => p.Page == 151);
        Assert.True(sample.Max(p => p.Page) - sample.Min(p => p.Page) > 150);
    }
}
