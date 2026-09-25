namespace Turian.Tests;

/// <summary>
/// Covers the Output panel's pure projection: what the severity toggles keep, how the text filter
/// narrows it, and how collapse merges consecutive duplicates behind a count without cutting a
/// duplicate run in two when a severity toggle hid one of its members.
/// </summary>
public class LogViewTests
{
    static LogLine Line(LogLevel level, string text) =>
        new(level, new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), text);

    /// <summary>The Errors toggle gates Fatal and Error, and nothing below.</summary>
    [Theory]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Warning, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Debug, false)]
    public void TheErrorsToggleGatesFatalAndError(LogLevel level, bool expected) =>
        Assert.Equal(expected, LogView.Visible(level, errors: true, warnings: false, log: false));

    /// <summary>The Warnings toggle gates Warning only.</summary>
    [Theory]
    [InlineData(LogLevel.Error, false)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Information, false)]
    public void TheWarningsToggleGatesWarning(LogLevel level, bool expected) =>
        Assert.Equal(expected, LogView.Visible(level, errors: false, warnings: true, log: false));

    /// <summary>The Debug toggle gates everything below warning — the whole log stream.</summary>
    [Theory]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Debug, true)]
    [InlineData(LogLevel.Trace, true)]
    [InlineData(LogLevel.Error, false)]
    [InlineData(LogLevel.Warning, false)]
    public void TheDebugToggleGatesTheLogStream(LogLevel level, bool expected) =>
        Assert.Equal(expected, LogView.Visible(level, errors: false, warnings: false, log: true));

    /// <summary>The text filter matches the message case-insensitively over every level.</summary>
    [Fact]
    public void TheTextFilterNarrowsByMessage()
    {
        var lines = new[]
        {
            Line(LogLevel.Information, "Loading scene"),
            Line(LogLevel.Warning, "Missing texture"),
            Line(LogLevel.Error, "texture failed"),
        };

        var rows = LogView.Project(lines, "TEXTURE", true, true, true, collapse: false);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Contains("texture", row.Line.Text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A toggle switch removes one severity lane; the others stay.</summary>
    [Fact]
    public void ALevelToggleHidesItsLane()
    {
        var lines = new[]
        {
            Line(LogLevel.Error, "boom"),
            Line(LogLevel.Warning, "watch out"),
            Line(LogLevel.Information, "all good"),
        };

        var rows = LogView.Project(lines, "", errors: false, warnings: true, log: true, collapse: false);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.Line.Level == LogLevel.Error);
    }

    /// <summary>Collapse merges consecutive duplicates into one row whose count says how many it hides.</summary>
    [Fact]
    public void CollapseCountsConsecutiveDuplicates()
    {
        var lines = new[]
        {
            Line(LogLevel.Warning, "same"),
            Line(LogLevel.Warning, "same"),
            Line(LogLevel.Warning, "same"),
            Line(LogLevel.Warning, "different"),
            Line(LogLevel.Warning, "same"),
        };

        var rows = LogView.Project(lines, "", true, true, true, collapse: true);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows[0].Count);
        Assert.Equal(1, rows[1].Count);
        Assert.Equal(1, rows[2].Count);
    }

    /// <summary>Two identical messages split by another level do not merge.</summary>
    [Fact]
    public void CollapseStopsAtALevelChange()
    {
        var lines = new[]
        {
            Line(LogLevel.Warning, "same"),
            Line(LogLevel.Error, "same"),
            Line(LogLevel.Warning, "same"),
        };

        var rows = LogView.Project(lines, "", true, true, true, collapse: true);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Count));
    }

    /// <summary>
    /// Collapse walks the filtered stream, so a toggle that hides one member of a run does not break
    /// the surviving members into separate rows.
    /// </summary>
    [Fact]
    public void CollapseOperatesOnTheFilteredStream()
    {
        var lines = new[]
        {
            Line(LogLevel.Warning, "same"),
            Line(LogLevel.Error, "same"),
            Line(LogLevel.Warning, "same"),
        };

        var rows = LogView.Project(lines, "", errors: false, warnings: true, log: true, collapse: true);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Count);
    }

    /// <summary>Every row carries its position in the raw buffer, so ids stay stable as rows shift.</summary>
    [Fact]
    public void RowsCarryTheirRawBufferIndex()
    {
        var lines = new[]
        {
            Line(LogLevel.Information, "one"),
            Line(LogLevel.Warning, "two"),
        };

        var rows = LogView.Project(lines, "", true, true, true, collapse: false);

        Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.RawIndex));
    }

    /// <summary>One line keeps the whole single-line message and caps a multi-line one to its first line.</summary>
    [Fact]
    public void FirstLinesWithOneLineCapsOverflow()
    {
        Assert.Equal("single", LogView.FirstLines("single", 1));
        Assert.Equal("a…", LogView.FirstLines("a\nb\nc", 1));
    }

    /// <summary>More lines keep that many, and a truncated message is marked with an ellipsis.</summary>
    [Fact]
    public void FirstLinesKeepsUpToTheRequestedCount()
    {
        Assert.Equal("a\nb…", LogView.FirstLines("a\nb\nc", 2));
        Assert.Equal("a\nb\nc", LogView.FirstLines("a\nb\nc", 3));
    }

    /// <summary>A message with no extra lines is returned unchanged, never with an ellipsis.</summary>
    [Fact]
    public void FirstLinesLeavesUntruncatedTextAlone()
    {
        Assert.Equal("", LogView.FirstLines("", 1));
        Assert.Equal("a\nb", LogView.FirstLines("a\nb", 2));
    }
}

/// <summary>Defaults and clamping of the Output console's persisted preferences.</summary>
public class OutputPanelSettingsTests
{
    /// <summary>The console starts with timestamps on, the clear-on … behaviors on, and one line per entry.</summary>
    [Fact]
    public void DefaultsAreASaneFreshConsole()
    {
        var settings = new OutputPanelSettings();

        Assert.True(settings.ShowTimestamp);
        Assert.False(settings.Monospace);
        Assert.True(settings.ClearOnPlay);
        Assert.True(settings.ClearOnBuild);
        Assert.True(settings.ClearOnRecompile);
        Assert.Equal(1, settings.EntryLines);
    }

    /// <summary>The Entry Lines stepper cannot leave the list showing nothing or an enormous entry.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100)]
    public void EntryLinesClampsToTheStepperRange(int value)
    {
        var settings = new OutputPanelSettings { EntryLines = value };

        Assert.InRange(settings.EntryLines, 1, 6);
    }

    /// <summary>Values inside the range are kept exactly.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void EntryLinesKeepsInRangeValues(int value)
    {
        Assert.Equal(value, new OutputPanelSettings { EntryLines = value }.EntryLines);
    }
}

/// <summary>
/// Covers how the Output panel recovers a source location from a message, in the two shapes a log
/// produces them: the <c>path(line,col):</c> form the MSBuild logger emits, and <c>path:line:</c>.
/// </summary>
public class LogSourceTests
{
    /// <summary>An MSBuild error line: file, line and column, then the diagnostic.</summary>
    [Fact]
    public void ParsesTheMsBuildParenForm()
    {
        var hit = LogSource.TryParse("C:\\proj\\Assets\\Camera.cs(42,8): error CS0246: missing type");

        Assert.NotNull(hit);
        Assert.Equal("C:\\proj\\Assets\\Camera.cs", hit.Value.Path);
        Assert.Equal(42, hit.Value.Line);
    }

    /// <summary>The paren form still works without the column, and on a bare relative name.</summary>
    [Fact]
    public void ParsesTheParenFormWithoutAColumn()
    {
        var hit = LogSource.TryParse("Program.cs(7): warning CS0219: unused");

        Assert.NotNull(hit);
        Assert.Equal("Program.cs", hit.Value.Path);
        Assert.Equal(7, hit.Value.Line);
    }

    /// <summary>The colon form carries no column; the path may itself contain colons.</summary>
    [Fact]
    public void ParsesTheColonForm()
    {
        var hit = LogSource.TryParse("src/App.cs:23: error CS0001");

        Assert.NotNull(hit);
        Assert.Equal("src/App.cs", hit.Value.Path);
        Assert.Equal(23, hit.Value.Line);
    }

    /// <summary>A message that mentions no location yields no source reference.</summary>
    [Theory]
    [InlineData("Loading assets...")]
    [InlineData("The (answer) is 42.")]
    [InlineData("")]
    public void MessagesWithoutALocationYieldNothing(string message) =>
        Assert.Null(LogSource.TryParse(message));

    /// <summary>A rooted path is used as-is when the file exists.</summary>
    [Fact]
    public void ResolvePathUsesARootedPathAsIs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turian-logsource-{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(path, "");
            Assert.Equal(path, LogSource.ResolvePath($"{path}(3,5): error CS0001", projectDirectory: null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A relative path is resolved against the open project's directory.</summary>
    [Fact]
    public void ResolvePathFindsTheFileInTheProject()
    {
        var project = Directory.CreateTempSubdirectory("turian-logsource-proj-");
        try
        {
            var relative = Path.Combine("Assets", "Camera.cs");
            var source = Path.Combine(project.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "");

            Assert.Equal(source, LogSource.ResolvePath($"{relative}(42,8): error CS0246", project.FullName));
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    /// <summary>Without a project directory, a relative path falls back to the process directory.</summary>
    [Fact]
    public void ResolvePathFallsBackToTheProcessDirectory()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), $"turian-logsource-{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(path, "");
            Assert.Equal(path, LogSource.ResolvePath($"{Path.GetFileName(path)}(5): warning CS0219", projectDirectory: null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A location whose file does not exist resolves to nothing, wherever it is looked up.</summary>
    [Fact]
    public void ResolvePathYieldsNothingForAMissingFile()
    {
        Assert.Null(LogSource.ResolvePath("Assets/Missing.cs(1): error CS0001", Directory.GetCurrentDirectory()));
        Assert.Null(LogSource.ResolvePath($"/nowhere/turian-logsource-{Guid.NewGuid():N}.cs(1): error CS0001", null));
    }

    /// <summary>A message without a location resolves to nothing even with a project directory.</summary>
    [Fact]
    public void ResolvePathYieldsNothingWithoutALocation() =>
        Assert.Null(LogSource.ResolvePath("Loading assets...", Directory.GetCurrentDirectory()));
}
