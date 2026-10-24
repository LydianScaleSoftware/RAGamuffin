using Ragamuffin.Stats;

namespace Ragamuffin.Tests;

public class StatsTests
{
    static PageMix Mix(int embedded = 0, int layout = 0, int ocrLayout = 0, int ocrWhole = 0, int blank = 0) =>
        new(embedded, layout, ocrLayout, ocrWhole, blank);

    [Theory]
    [InlineData("markdown")]
    [InlineData("text")]
    [InlineData("code")]
    public void Non_pdf_documents_are_original_text(string type)
    {
        Assert.Equal(TextOrigin.OriginalText, StatsService.Classify(type, null));
    }

    [Fact]
    public void Classifies_pdfs_by_where_their_page_text_comes_from()
    {
        Assert.Equal(TextOrigin.PdfEmbedded, StatsService.Classify("pdf", Mix(embedded: 10)));
        Assert.Equal(TextOrigin.PdfEmbeddedLayout, StatsService.Classify("pdf", Mix(embedded: 3, layout: 7)));
        Assert.Equal(TextOrigin.PdfOcr, StatsService.Classify("pdf", Mix(ocrLayout: 5, ocrWhole: 1, blank: 2)));
        Assert.Equal(TextOrigin.PdfMixed, StatsService.Classify("pdf", Mix(layout: 4, ocrLayout: 2)));
        Assert.Equal(TextOrigin.PdfMixed, StatsService.Classify("pdf", Mix(embedded: 4, ocrWhole: 1)));
        Assert.Equal(TextOrigin.PdfNoText, StatsService.Classify("pdf", Mix(blank: 9)));
        Assert.Equal(TextOrigin.PdfNoText, StatsService.Classify("pdf", null));
    }

    [Fact]
    public void Page_mixes_add_up()
    {
        var sum = Mix(1, 2, 3, 4, 5) + Mix(10, 20, 30, 40, 50);
        Assert.Equal(Mix(11, 22, 33, 44, 55), sum);
        Assert.Equal(77, sum.Ocr);
        Assert.Equal(165, sum.Total);
    }

    [Fact]
    public void Group_derives_averages_and_remaining_work()
    {
        var group = new StatsGroup("c", false, Documents: 4, Pages: 20, Chunks: 10, SizeBytes: 0, Characters: 12_000,
            Types: [], Languages: [], Origins: [], PdfPages: Mix(embedded: 6, layout: 8, blank: 6), Statuses: [],
            SuspectPages: 0, ProblemPages: 0, Claudomatics: 2, ClaudomaticChars: 5_000, EncodedDocuments: 0, HiddenChunks: 0,
            DocumentsWithNotices: 0, AddedLast7Days: 0, LastUpdated: null);
        Assert.Equal(2.5, group.AvgChunksPerDocument);
        Assert.Equal(1200, group.AvgChunkChars);
        Assert.Equal(6, group.PagesNeedingOcr);
        Assert.Equal(6, group.PagesWithoutLayout);
        Assert.Equal(2500, group.AvgClaudomaticChars);
    }

    [Fact]
    public void Empty_group_has_zero_averages()
    {
        var group = new StatsGroup("c", false, 0, 0, 0, 0, 0, [], [], [], PageMix.Empty, [], 0, 0, 0, 0, 0, 0, 0, 0, null);
        Assert.Equal(0, group.AvgChunksPerDocument);
        Assert.Equal(0, group.AvgChunkChars);
        Assert.Equal(0, group.AvgClaudomaticChars);
    }
}
