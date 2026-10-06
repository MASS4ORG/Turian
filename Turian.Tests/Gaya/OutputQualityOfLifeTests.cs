namespace Turian.Tests;

/// <summary>Checks log origins, independent severity filters and navigation from recorded events.</summary>
[Collection(SerialTests.Name)]
public sealed class OutputQualityOfLifeTests
{
    /// <summary>Information and Debug can each be enabled while the other is hidden.</summary>
    [Theory]
    [InlineData(LogLevel.Information, true, false, true)]
    [InlineData(LogLevel.Debug, true, false, false)]
    [InlineData(LogLevel.Information, false, true, false)]
    [InlineData(LogLevel.Trace, false, true, true)]
    [InlineData(LogLevel.None, true, true, false)]
    public void InformationHasItsOwnFilter(LogLevel level, bool info, bool debug, bool visible) =>
        Assert.Equal(visible, LogView.Visible(level, false, false, debug, info));

    /// <summary>Hiding Studio preserves project messages at every enabled severity.</summary>
    [Theory]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    public void StudioFilterPreservesProjectMessages(LogLevel level)
    {
        var internalLine = new LogLine(level, DateTimeOffset.Now, "studio") { IsInternal = true };
        var projectLine = new LogLine(level, DateTimeOffset.Now, "project");
        var rows = LogView.Project([internalLine, projectLine], "", true, true, true, false, true, false);
        Assert.Equal(projectLine, Assert.Single(rows).Line);
    }

    /// <summary>Identical messages from different origins retain their own navigation targets.</summary>
    [Fact]
    public void CollapseKeepsDistinctOrigins()
    {
        var line = new LogLine(LogLevel.Information, DateTimeOffset.Now, "hello")
        {
            Category = "Game",
            SourceFile = "Game.cs",
            SourceLine = 10,
        };
        Assert.Equal(4, LogView.Project([line, line with { Category = "Editor" },
            line with { SourceFile = "Other.cs" }, line with { SourceLine = 11 }],
            "", true, true, true, true).Count);
    }

    /// <summary>The buffer records a caller file and line even when the message has no location.</summary>
    [Theory]
    [InlineData("Game", false)]
    [InlineData("Turian", false)]
    [InlineData("Editor", true)]
    [InlineData("Gaya.Host.Workbench", true)]
    public void BufferRetainsOriginAndSource(string category, bool internalMessage)
    {
        LogBuffer.Clear();
        try
        {
            LogBuffer.Provider.CreateLogger(category).LogInformation("hello from source");
            var line = Assert.Single(LogBuffer.Snapshot());
            Assert.Equal(category, line.Category);
            Assert.Equal(internalMessage, line.IsInternal);
            Assert.EndsWith("OutputQualityOfLifeTests.cs", line.SourceFile);
            Assert.True(line.SourceLine > 0);
            Assert.Equal((line.SourceFile!, line.SourceLine), LogSource.TryParse(LogSource.LocationMessage(line)));
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>The shared logger distinguishes Studio's caller from project code's caller.</summary>
    [Fact]
    public void AmbientCategoryPreservesCallerOwnership()
    {
        LogBuffer.Clear();
        try
        {
            var logger = LogBuffer.Provider.CreateLogger("Turian");
            logger.LogInformation("project callback");
            LogSource.Open("/missing/Source.cs(1): diagnostic", logger);
            var lines = LogBuffer.Snapshot();
            Assert.False(lines[0].IsInternal);
            Assert.True(lines[1].IsInternal);
            Assert.EndsWith("LogSource.cs", lines[1].SourceFile);
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>Exception stack traces supply navigable file and line coordinates.</summary>
    [Fact]
    public void ExceptionStackLocationIsRecognized()
    {
        var hit = LogSource.TryParse("failed\n   at Game.Update() in /project/Assets/Game.cs:line 42");
        Assert.Equal(("/project/Assets/Game.cs", 42), hit);
    }

    /// <summary>An explicit diagnostic location takes precedence over the logging call site.</summary>
    [Fact]
    public void ExplicitSourceWins()
    {
        var line = new LogLine(LogLevel.Error, DateTimeOffset.Now, "Assets/Broken.cs(7): failure")
        {
            SourceFile = "Logger.cs",
            SourceLine = 12,
        };
        Assert.Equal(line.Text, LogSource.LocationMessage(line));
    }

    /// <summary>A message without symbols or an explicit location stays unchanged.</summary>
    [Fact]
    public void MissingSourceStaysUnchanged() => Assert.Equal("hello",
        LogSource.LocationMessage(new LogLine(LogLevel.Information, DateTimeOffset.Now, "hello")));

    /// <summary>Each editor receives its supported line syntax as literal process arguments.</summary>
    [Theory]
    [InlineData("code")]
    [InlineData("codium")]
    [InlineData("cursor")]
    [InlineData("zed")]
    public void EditorsReceiveSupportedSourceArguments(string editor)
    {
        var launches = new List<ProcessStartInfo>();
        LogSource.LaunchSource("/project with spaces/Game.cs", 42, NullLogger.Instance, candidate => candidate == editor,
            info => { launches.Add(info); return null; });
        var launch = Assert.Single(launches);
        Assert.Equal(editor, launch.FileName);
        Assert.False(launch.UseShellExecute);
        Assert.Equal(editor == "zed" ? ["/project with spaces/Game.cs:42"]
            : new[] { "--goto", "/project with spaces/Game.cs:42" }, launch.ArgumentList);
    }

    /// <summary>The desktop fallback runs when no editor is available or the editor cannot launch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorLaunchFailureUsesDesktopFallback(bool available)
    {
        var launches = new List<ProcessStartInfo>();
        LogSource.LaunchSource("Game.cs", 7, NullLogger.Instance, _ => available, info =>
        {
            launches.Add(info);
            if (!info.UseShellExecute) throw new InvalidOperationException("editor unavailable");
            return null;
        });
        Assert.Equal(available ? 2 : 1, launches.Count);
        Assert.True(launches[^1].UseShellExecute);
        Assert.Equal("Game.cs", launches[^1].FileName);
    }

    /// <summary>A failed desktop launch is reported without interrupting the editor's render loop.</summary>
    [Fact]
    public void DesktopLaunchFailureDoesNotEscape() => LogSource.LaunchSource("Game.cs", 7, NullLogger.Instance,
        _ => false, _ => throw new InvalidOperationException("no association"));
}
