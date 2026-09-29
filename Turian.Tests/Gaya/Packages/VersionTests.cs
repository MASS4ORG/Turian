namespace Turian.Tests;

/// <summary>Semantic versions and npm-style ranges, as package dependencies state them.</summary>
public class VersionTests
{
    /// <summary>Versions order by SemVer precedence: prereleases first, build metadata ignored.</summary>
    [Fact]
    public void VersionsOrderBySemVerPrecedence()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0", "1.2.0", "2.0.0"];

        var parsed = ordered.Select(SemanticVersion.Parse).ToList();

        Assert.Equal(parsed, parsed.Order());
        Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
        Assert.Equal(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("v1.2.3"));
    }

    /// <summary>Text that is not a version is rejected.</summary>
    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("a.b.c")]
    public void InvalidVersionsAreRejected(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    /// <summary>Ranges accept exactly the versions npm would.</summary>
    [Theory]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("1.2", "1.2.7", true)]
    [InlineData("1.2", "1.3.0", false)]
    [InlineData("1.x", "1.9.9", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData(">=1.0.0 <2.0.0", "1.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false)]
    [InlineData("^1.0.0 || ^3.0.0", "3.1.0", true)]
    [InlineData("^1.0.0 || ^3.0.0", "2.1.0", false)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("^1.0.0", "1.5.0-beta", false)]
    [InlineData("^1.5.0-beta", "1.5.0-rc", true)]
    [InlineData("^1.5.0-beta", "1.6.0-rc", false)]
    public void RangesMatchLikeNpm(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Parse(range).IsSatisfiedBy(SemanticVersion.Parse(version)));
}
