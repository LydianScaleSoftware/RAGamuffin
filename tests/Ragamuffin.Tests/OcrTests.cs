using Ragamuffin.Data;
using Ragamuffin.Ingestion;
using Ragamuffin.Ocr;
using SkiaSharp;

namespace Ragamuffin.Tests;

public class PdfPagesTests
{
    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    static DocumentPage Page(int n, string? embedded, string? ocr = null, string source = PageSource.Embedded, string? suspect = null) =>
        new() { Page = n, EmbeddedText = embedded, OcrText = ocr, Source = source, SuspectReason = suspect };

    const string Words = "The coolant pump on unit B7 must be replaced every 4000 operating hours.";

    [Fact]
    public void Scanned_pdf_has_a_page_without_text()
    {
        var pages = PdfPages.Extract(Fixture("scanned.pdf"));
        var page = Assert.Single(pages);
        Assert.False(PdfPages.HasUsefulText(page.Text));
        Assert.Null(page.SuspectReason);
    }

    [Fact]
    public void Text_pdf_pages_have_text_and_are_not_suspect()
    {
        var pages = PdfPages.Extract(Fixture("text.pdf"));
        Assert.Equal(2, pages.Count);
        Assert.Contains("coolant pump", pages[0].Text);
        Assert.All(pages, p => Assert.Null(p.SuspectReason));
    }

    [Fact]
    public void Text_extractor_rejects_a_scanned_pdf()
    {
        Assert.Throws<ExtractionException>(() => TextExtractor.Extract("scan.pdf", Fixture("scanned.pdf")));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   12  ", false)]
    [InlineData("A slightly longer caption here.", true)]
    public void Useful_text_needs_a_minimum_of_characters(string text, bool useful)
    {
        Assert.Equal(useful, PdfPages.HasUsefulText(text));
    }

    [Fact]
    public void Invisible_text_over_a_scan_is_suspect()
    {
        Assert.Contains("Invisible", PdfPages.SuspectReason(invisibleShare: 0.95, imageCover: 1, null, Words));
        Assert.Null(PdfPages.SuspectReason(invisibleShare: 0.95, imageCover: 0.2, null, Words));
    }

    [Fact]
    public void Scanned_page_from_ocr_software_is_suspect()
    {
        Assert.Contains("abbyy", PdfPages.SuspectReason(0, 0.9, "abbyy", Words));
        Assert.Null(PdfPages.SuspectReason(0, 0.3, "abbyy", Words));
    }

    [Theory]
    [InlineData("ABBYY FineReader 11", "abbyy")]
    [InlineData("Tesseract 4.1.1", "tesseract")]
    [InlineData("ocrmypdf 14.0 / Ghostscript", "ocrmypdf")]
    [InlineData("Scanner OCR module", "ocr")]
    [InlineData("Microsoft Word for Microsoft 365", null)]
    [InlineData("Procrastination Press", null)]
    [InlineData(null, null)]
    public void Recognizes_ocr_producers(string? metadata, string? expected)
    {
        Assert.Equal(expected, PdfPages.OcrProducerName(metadata));
    }

    [Fact]
    public void Clean_prose_is_not_garbled()
    {
        var text = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog, then naps.", 6));
        Assert.False(PdfPages.LooksGarbled(text));
    }

    [Fact]
    public void Technical_text_is_not_garbled()
    {
        var text = string.Join(' ', Enumerable.Repeat("Load $00:FFFC into PC; see CB-12, V1.2, e-mail ops@example.com and docs/setup.md.", 5));
        Assert.False(PdfPages.LooksGarbled(text));
    }

    [Fact]
    public void Symbol_noise_inside_words_is_garbled()
    {
        var text = string.Join(' ', Enumerable.Repeat("Th~e qu|ck br%wn f^x jum;ps ov€r th# l@zy d*g aga!n", 5));
        Assert.True(PdfPages.LooksGarbled(text));
    }

    [Fact]
    public void Letter_spaced_text_is_garbled()
    {
        var text = string.Join("  ", Enumerable.Repeat("t h e q u i c k b r o w n f o x", 4));
        Assert.True(PdfPages.LooksGarbled(text));
    }

    [Fact]
    public void Short_text_is_never_judged()
    {
        Assert.False(PdfPages.LooksGarbled("Th~e qu|ck br%wn"));
    }

    [Fact]
    public void Selects_pages_for_each_scope()
    {
        var pages = new[]
        {
            Page(1, Words),
            Page(2, ""),
            Page(3, Words, suspect: "Text looks garbled"),
            Page(4, "", Words, PageSource.Ocr),
            Page(5, Words, Words, PageSource.Ocr, suspect: "Text looks garbled"),
        };
        Assert.Equal([2], PdfPages.SelectForOcr(pages, OcrScope.MissingText));
        Assert.Equal([2, 3], PdfPages.SelectForOcr(pages, OcrScope.Suspect));
        Assert.Equal([1, 2, 3, 4, 5], PdfPages.SelectForOcr(pages, OcrScope.All));
    }

    [Fact]
    public void Extracted_text_uses_each_pages_source_and_skips_blank_pages()
    {
        var doc = PdfPages.ToExtracted([Page(2, "embedded page two text here", "ocr two"), Page(1, "", "OCR text of the first page", PageSource.Ocr), Page(3, "")]);
        Assert.Equal([1, 2], doc.Pages!.Select(p => p.Page));
        Assert.Equal("OCR text of the first page", doc.Pages![0].Text);
        Assert.Equal("embedded page two text here", doc.Pages![1].Text);
        Assert.StartsWith("[Page 1]", doc.Text);
    }
}

public class TextSanitizerTests
{
    [Fact]
    public void Removes_nul_and_replaces_control_characters()
    {
        Assert.Equal("abc d\ne\tf", TextSanitizer.Clean("a\0bc\u0007d\ne\tf"));
    }

    [Fact]
    public void Replaces_unpaired_surrogates_but_keeps_pairs()
    {
        Assert.Equal("x�y😀", TextSanitizer.Clean("x\uD800y😀"));
    }

    [Fact]
    public void Leaves_clean_text_untouched()
    {
        const string text = "Plain text, ünïcode and\nnew lines.";
        Assert.Same(text, TextSanitizer.Clean(text));
    }
}

public class OcrEngineTests
{
    [Theory]
    [InlineData("```markdown\n# Title\nBody\n```", "# Title\nBody")]
    [InlineData("  Plain text  \r\nnext  ", "Plain text\nnext")]
    [InlineData("```markdown\n\n```", "")]
    [InlineData(null, "")]
    [InlineData("Uses ``` inline", "Uses ``` inline")]
    public void Cleans_model_output(string? raw, string expected)
    {
        Assert.Equal(expected, OcrEngine.CleanTranscription(raw));
    }

    [Theory]
    [InlineData("glm-ocr:latest", "glm-ocr", true)]
    [InlineData("glm-ocr:q8_0", "glm-ocr", false)]
    [InlineData("glm-ocr:q8_0", "glm-ocr:q8_0", true)]
    [InlineData("qwen3.8:27b", "QWEN3.8:27b", true)]
    [InlineData("glm-ocr-large:latest", "glm-ocr", false)]
    public void Matches_installed_model_names(string installed, string configured, bool same)
    {
        Assert.Equal(same, OcrEngine.SameModel(installed, configured));
    }

    [Fact]
    public void Content_bounds_trim_blank_margins()
    {
        using var bitmap = new SKBitmap(1000, 1400);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(100, 50, 600, 100, paint);
        }
        var box = OcrEngine.ContentBounds(bitmap);
        Assert.NotNull(box);
        Assert.InRange(box.Value.Left, 70, 100);
        Assert.InRange(box.Value.Top, 20, 50);
        Assert.InRange(box.Value.Right, 700, 730);
        Assert.InRange(box.Value.Bottom, 150, 180);
    }

    [Fact]
    public void Content_bounds_skip_blank_and_full_pages()
    {
        using var blank = new SKBitmap(200, 200);
        using (var c = new SKCanvas(blank)) c.Clear(SKColors.White);
        Assert.Null(OcrEngine.ContentBounds(blank));

        using var full = new SKBitmap(200, 200);
        using (var c = new SKCanvas(full)) c.Clear(SKColors.Gray);
        Assert.Null(OcrEngine.ContentBounds(full));
    }
}
