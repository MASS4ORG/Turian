namespace Turian.Tests;

/// <summary>Checks Studio's logging default and explicit argument overrides.</summary>
public sealed class StudioLogOptionsTests
{
    /// <summary>Project and headless options leave the default at Information.</summary>
    [Fact]
    public void DefaultsToInformation() => Assert.Equal(LogLevel.Information,
        StudioLogOptions.MinimumLevel(["--dump", "frame.png", "--project", "project"]));

    /// <summary>Each named level can override the minimum independently of its letter case.</summary>
    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    public void AcceptsNamedLevels(string value, LogLevel expected) => Assert.Equal(expected,
        StudioLogOptions.MinimumLevel(["--project", "project", "--log-level", value]));

    /// <summary>Invalid or missing levels are rejected before the Studio host starts.</summary>
    [Theory]
    [InlineData("verbose")]
    [InlineData("42")]
    [InlineData("1")]
    [InlineData("Information,Debug")]
    [InlineData("")]
    public void RejectsInvalidLevels(string value) => Assert.Throws<ArgumentException>(() =>
        StudioLogOptions.MinimumLevel(["--log-level", value]));

    /// <summary>A level option requires its following value.</summary>
    [Fact]
    public void RejectsMissingLevel() => Assert.Throws<ArgumentException>(() =>
        StudioLogOptions.MinimumLevel(["--log-level"]));
}
