using RagServer.Collections;

namespace RagServer.Tests;

public class CollectionRulesTests
{
    static readonly string[] Existing = ["default", "alpha", "beta", "gamma"];

    [Theory]
    [InlineData("Homelab notes")]
    [InlineData("  padded  ")]
    [InlineData("ünïcode/with-symbols_1")]
    public void Accepts_reasonable_names(string name)
    {
        Assert.Null(CollectionRules.ValidateName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData(" Default ")]
    [InlineData("tab\there")]
    public void Rejects_bad_names(string? name)
    {
        Assert.NotNull(CollectionRules.ValidateName(name));
    }

    [Fact]
    public void Rejects_overlong_name()
    {
        Assert.NotNull(CollectionRules.ValidateName(new string('a', CollectionRules.MaxNameLength + 1)));
        Assert.Null(CollectionRules.ValidateName(new string('a', CollectionRules.MaxNameLength)));
    }

    [Fact]
    public void Allows_swapping_names_in_one_batch()
    {
        var errors = CollectionRules.ValidateRenames(Existing, [new("alpha", "beta"), new("beta", "alpha")]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Allows_chained_renames()
    {
        var errors = CollectionRules.ValidateRenames(Existing, [new("alpha", "beta"), new("beta", "delta")]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Rejects_rename_onto_an_untouched_collection()
    {
        var errors = CollectionRules.ValidateRenames(Existing, [new("alpha", "gamma")]);
        Assert.Contains(errors, e => e.Contains("gamma"));
    }

    [Fact]
    public void Rejects_two_renames_to_the_same_name()
    {
        var errors = CollectionRules.ValidateRenames(Existing, [new("alpha", "new"), new("beta", "new")]);
        Assert.Contains(errors, e => e.Contains("\"new\""));
    }

    [Fact]
    public void Reports_every_invalid_entry()
    {
        var errors = CollectionRules.ValidateRenames(Existing,
            [new("default", "x"), new("missing", "y"), new("alpha", ""), new("beta", "b2"), new("beta", "b3")]);
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Names_are_case_sensitive()
    {
        Assert.Empty(CollectionRules.ValidateRenames(Existing, [new("alpha", "Alpha")]));
    }
}
