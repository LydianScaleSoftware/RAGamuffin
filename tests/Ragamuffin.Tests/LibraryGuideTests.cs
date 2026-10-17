using Ragamuffin.Collections;
using Ragamuffin.Mcp;

namespace Ragamuffin.Tests;

public class CollectionDetailsTests
{
    [Fact]
    public void Keywords_are_trimmed_deduplicated_and_capped()
    {
        var keywords = CollectionRules.ParseKeywords(" IIGS, Toolbox ; iigs,\n  QuickDraw   II , , " + new string('x', 80));
        Assert.Equal(["IIGS", "Toolbox", "QuickDraw II", new string('x', CollectionRules.MaxKeywordLength)], keywords);
        Assert.Equal(CollectionRules.MaxKeywords, CollectionRules.NormalizeKeywords(Enumerable.Range(0, 50).Select(i => $"k{i}")).Count);
        Assert.Empty(CollectionRules.NormalizeKeywords(null));
    }

    [Fact]
    public void Descriptions_collapse_whitespace_and_empty_becomes_null()
    {
        Assert.Equal("Apple IIGS manuals.", CollectionRules.NormalizeDescription("  Apple   IIGS\n manuals. "));
        Assert.Null(CollectionRules.NormalizeDescription("   "));
        Assert.Equal(CollectionRules.MaxDescriptionLength, CollectionRules.NormalizeDescription(new string('a', 2000))!.Length);
    }

    [Fact]
    public void Parses_the_models_json_answer()
    {
        var s = CollectionSuggester.Parse("```json\n{\"description\": \"IIGS manuals.\", \"keywords\": [\"IIGS\", \"Toolbox\", \"iigs\"]}\n```");
        Assert.Equal("IIGS manuals.", s.Description);
        Assert.Equal(["IIGS", "Toolbox"], s.Keywords);
    }

    [Fact]
    public void Accepts_keywords_given_as_one_string()
    {
        Assert.Equal(["ORCA/C", "GoldenGate"], CollectionSuggester.Parse("{\"description\":\"x\",\"keywords\":\"ORCA/C, GoldenGate\"}").Keywords);
    }

    [Fact]
    public void Rejects_answers_without_json()
    {
        Assert.Throws<SuggestionException>(() => CollectionSuggester.Parse("Sorry, I can't help with that."));
        Assert.Throws<SuggestionException>(() => CollectionSuggester.Parse("{ not json }"));
    }
}

public class LibraryGuideTests
{
    static CollectionSummary C(string name, int docs, string? description = null, int keywords = 0, int titles = 0) => new(
        name, docs, docs * 10, description,
        Enumerable.Range(0, keywords).Select(i => $"keyword{i}").ToList(),
        new Dictionary<string, int> { ["pdf"] = docs },
        Enumerable.Range(0, titles).Select(i => $"Example title {i}").ToList());

    [Fact]
    public void Instructions_name_the_tools_and_each_collection()
    {
        var text = LibraryGuide.BuildInstructions(
            [C("Apple 2gs Manuals", 188, "Apple IIGS hardware, firmware and Toolbox references.", 4), C("orca/goldengate", 42)], 512);
        Assert.Contains("search_documents", text);
        Assert.Contains("list_collections", text);
        Assert.Contains("- Apple 2gs Manuals (188 docs): Apple IIGS hardware, firmware and Toolbox references. Keywords: keyword0, keyword1, keyword2, keyword3.", text);
        Assert.Contains("- orca/goldengate (42 docs)", text);
        Assert.Contains("230 documents in 2 collections", text);
    }

    [Fact]
    public void Empty_library_says_so()
    {
        Assert.Contains("empty", LibraryGuide.BuildInstructions([], 512));
        Assert.Equal("The library is empty.", LibraryGuide.BuildList([], 512, 5));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(80)]
    public void Instructions_stay_within_the_token_budget(int collections)
    {
        var many = Enumerable.Range(0, collections)
            .Select(i => C($"Collection number {i}", 100 - i, string.Join(' ', Enumerable.Repeat("long description words", 40)), 20, 10))
            .ToList();
        var text = LibraryGuide.BuildInstructions(many, 512);
        Assert.True(LibraryGuide.EstimateTokens(text) <= 512, $"{LibraryGuide.EstimateTokens(text)} tokens");
        Assert.Contains("Collection number 0", text); // the largest collections are kept
    }

    [Fact]
    public void Too_many_collections_are_summarized_as_more()
    {
        var many = Enumerable.Range(0, 80).Select(i => C($"Collection number {i}", 100 - i, "Some description.", 5)).ToList();
        var text = LibraryGuide.BuildInstructions(many, 512);
        Assert.Matches(@"…and \d+ more collections \(call list_collections\)", text);
    }

    [Fact]
    public void Detail_shrinks_before_collections_are_dropped()
    {
        var two = new[] { C("A", 5, string.Join(' ', Enumerable.Repeat("word", 300)), 20), C("B", 3, "Short.", 2) };
        var text = LibraryGuide.BuildInstructions(two, 512);
        Assert.Contains("- A (5 docs)", text);
        Assert.Contains("- B (3 docs)", text);
        Assert.Contains("…", text);
        Assert.True(LibraryGuide.EstimateTokens(text) <= 512);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void List_stays_within_the_token_budget_and_includes_examples_when_room(int collections)
    {
        var many = Enumerable.Range(0, collections).Select(i => C($"Collection {i}", 50, "Manuals and references.", 8, 10)).ToList();
        var text = LibraryGuide.BuildList(many, 512, titles: 10);
        Assert.True(LibraryGuide.EstimateTokens(text) <= 512, $"{LibraryGuide.EstimateTokens(text)} tokens");
        if (collections == 1) Assert.Contains("Examples: Example title 0", text);
    }
}
