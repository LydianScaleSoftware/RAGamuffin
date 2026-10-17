using System.Text;
using Ragamuffin.Ingestion;
using Ragamuffin.Search;

namespace Ragamuffin.Tests;

public class TextExtractorTests
{
    [Theory]
    [InlineData("readme.md", "markdown", null)]
    [InlineData("src/Program.cs", "code", "csharp")]
    [InlineData("script.py", "code", "python")]
    [InlineData("Dockerfile", "code", "dockerfile")]
    [InlineData("notes.txt", "text", null)]
    [InlineData("LICENSE", "text", null)]
    [InlineData("paper.PDF", "pdf", null)]
    public void Classifies_by_file_name(string name, string type, string? language)
    {
        Assert.Equal((type, language), TextExtractor.Classify(name));
    }

    [Fact]
    public void Decodes_utf8_with_bom_and_normalizes_newlines()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\r\nb\rc")).ToArray();
        Assert.Equal("a\nb\nc", TextExtractor.DecodeText(bytes));
    }

    [Fact]
    public void Decodes_utf16()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo")).ToArray();
        Assert.Equal("héllo", TextExtractor.DecodeText(bytes));
    }

    [Fact]
    public void Rejects_binary()
    {
        Assert.Null(TextExtractor.DecodeText([0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01]));
        Assert.Throws<ExtractionException>(() => TextExtractor.Extract("a.bin", [1, 0, 2]));
    }

    [Fact]
    public void Invalid_pdf_throws_extraction_exception()
    {
        Assert.Throws<ExtractionException>(() => TextExtractor.Extract("broken.pdf", Encoding.ASCII.GetBytes("not a pdf")));
    }
}

public class FusionTests
{
    [Fact]
    public void Items_found_by_both_rank_first()
    {
        var fused = SearchService.Fuse(semantic: [1, 2, 3], keyword: [3, 4], topK: 10);
        Assert.Equal(3, fused[0].Id);
        Assert.Equal(1, fused[1].Id);
        Assert.Equal(4, fused.Count);
    }

    [Fact]
    public void Respects_top_k()
    {
        var fused = SearchService.Fuse([1, 2, 3, 4, 5], [], topK: 2);
        Assert.Equal([1L, 2L], fused.Select(f => f.Id));
    }
}
