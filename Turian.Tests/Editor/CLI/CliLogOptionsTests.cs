namespace Turian.Tests;

/// <summary>Verifies the CLI logging default and explicit verbosity controls.</summary>
public sealed class CliLogOptionsTests
{
    /// <summary>A source build keeps routine diagnostics at Information.</summary>
    [Fact]
    public void DefaultsToInformation() => Assert.Equal(LogLevel.Information,
        global::Turian.Editor.CLI.CliLogOptions.MinimumLevel(["deploy", "project", "--profile", "Android Client"]));

    /// <summary>Both option spellings accept named levels and equals syntax.</summary>
    [Theory]
    [InlineData("--verbosity", "Debug", LogLevel.Debug)]
    [InlineData("--log-level", "trace", LogLevel.Trace)]
    [InlineData("--verbosity=Warning", null, LogLevel.Warning)]
    [InlineData("--log-level=None", null, LogLevel.None)]
    public void AcceptsExplicitLevels(string option, string? value, LogLevel expected) => Assert.Equal(expected,
        global::Turian.Editor.CLI.CliLogOptions.MinimumLevel(value is null ? [option] : [option, value]));

    /// <summary>Malformed verbosity fails before any project is prepared.</summary>
    [Theory]
    [InlineData("--verbosity")]
    [InlineData("--verbosity=42")]
    [InlineData("--verbosity=verbose")]
    public void RejectsInvalidLevels(string option) => Assert.Throws<ArgumentException>(() =>
        global::Turian.Editor.CLI.CliLogOptions.MinimumLevel([option]));

    /// <summary>Arguments after the option terminator do not change logging.</summary>
    [Fact]
    public void RespectsTheOptionTerminator() => Assert.Equal(LogLevel.Information,
        global::Turian.Editor.CLI.CliLogOptions.MinimumLevel(["--", "--verbosity", "Debug"]));
}
