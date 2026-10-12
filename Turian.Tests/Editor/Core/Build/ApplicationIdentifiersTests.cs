namespace Turian.Tests;

/// <summary>Application identifiers as Java package names: sanitizing project titles and validating saved ids.</summary>
public sealed class ApplicationIdentifiersTests
{
    /// <summary>Titles become lowercase ASCII segments that start with a letter and avoid Java keywords.</summary>
    [Theory]
    [InlineData("My Game", "my_game")]
    [InlineData("space-shooter", "space_shooter")]
    [InlineData("2D Platformer", "g2d_platformer")]
    [InlineData("class", "class_")]
    [InlineData("Café", "caf")]
    [InlineData("日本語", "game")]
    [InlineData("  ", "game")]
    public void SegmentsAreSanitized(string title, string expected)
    {
        var segment = ApplicationIdentifiers.SanitizeSegment(title);
        Assert.Equal(expected, segment);
        Assert.Null(ApplicationIdentifiers.Validate($"com.turian.{segment}"));
    }

    /// <summary>Valid package names pass, including underscores and digits after the first character.</summary>
    [Theory]
    [InlineData("com.example.game")]
    [InlineData("com.Example.Game_2")]
    [InlineData("a.b")]
    public void ValidIdentifiersPass(string identifier) => Assert.Null(ApplicationIdentifiers.Validate(identifier));

    /// <summary>Each way an identifier breaks the Java rules gets a message naming the problem.</summary>
    [Theory]
    [InlineData(null, "empty")]
    [InlineData("  ", "empty")]
    [InlineData("game", "two")]
    [InlineData("com.my-game", "my-game")]
    [InlineData("com.2d.game", "2d")]
    [InlineData("com..game", "invalid segment ''")]
    [InlineData("com.café.game", "café")]
    [InlineData("com.example.new", "reserved Java word")]
    public void InvalidIdentifiersAreReported(string? identifier, string fragment)
    {
        var message = ApplicationIdentifiers.Validate(identifier);
        Assert.NotNull(message);
        Assert.Contains(fragment, message);
    }

    /// <summary>The desktop platform accepts any identifier, so existing projects export unchanged.</summary>
    [Fact]
    public void DesktopAcceptsAnyIdentifier()
    {
        var settings = new BuildAppSettings { Title = "Game" };
        Assert.Null(BuildPlatforms.Find(BuildPlatforms.Desktop).ValidateSettings(settings));
    }
}
