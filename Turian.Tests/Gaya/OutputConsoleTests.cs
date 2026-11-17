namespace Turian.Tests;

/// <summary>Covers the Output console's selection model, clickable stack frames, severity icons and Studio filter.</summary>
[Collection(SerialTests.Name)]
public sealed class OutputConsoleTests
{
    const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    static LogRow Row(int raw, string text) =>
        new(new LogLine(LogLevel.Information, DateTimeOffset.Now, text), 1, raw);

    /// <summary>A stack frame inside an existing file becomes a link covering just the file and line.</summary>
    [Fact]
    public void ExistingStackFramesBecomeLinks()
    {
        var file = Path.GetTempFileName();
        try
        {
            var text = $"failed\n   at Game.Update() in {file}:line 42\n   at Engine.Run() in /missing/Engine.cs:line 9";

            var link = Assert.Single(LogSource.Links(text, null));

            Assert.Equal($"{file}:line 42", text.Substring(link.Start, link.Length));
            Assert.Equal($"   at Game.Update() in {file}:line 42", link.Line);
        }
        finally { File.Delete(file); }
    }

    /// <summary>A compiler diagnostic links its file and position, not the message after it.</summary>
    [Fact]
    public void DiagnosticLocationsBecomeLinks()
    {
        var file = Path.GetTempFileName();
        try
        {
            var text = $"{file}(7,3): error CS1002: ; expected";

            var link = Assert.Single(LogSource.Links(text, null));

            Assert.Equal($"{file}(7,3)", text.Substring(link.Start, link.Length));
        }
        finally { File.Delete(file); }
    }

    /// <summary>Project-relative frames resolve against the open project's folder.</summary>
    [Fact]
    public void RelativeFramesResolveAgainstTheProject()
    {
        var project = Path.Combine(Path.GetTempPath(), $"turian-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        File.WriteAllText(Path.Combine(project, "Assets", "Game.cs"), "");
        try
        {
            Assert.Single(LogSource.Links("Assets/Game.cs(3): warning", project));
            Assert.Empty(LogSource.Links("Assets/Game.cs(3): warning", null));
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    /// <summary>Text without a source location has no links, and a missing file is never linked.</summary>
    [Fact]
    public void PlainTextAndMissingFilesHaveNoLinks()
    {
        Assert.Empty(LogSource.Links("hello\nworld", null));
        Assert.Empty(LogSource.Links("/missing/Game.cs(7): first", null));
        Assert.Null(LogSource.Locate(""));
    }

    /// <summary>A plain click selects one entry; Ctrl+click adds and removes entries.</summary>
    [Fact]
    public void ClickAndToggleBuildTheSelection()
    {
        var rows = new[] { Row(10, "a"), Row(11, "b"), Row(12, "c") };
        var selection = new LogSelection();

        selection.Set(rows, 0);
        selection.Toggle(rows, 2);
        Assert.Equal(2, selection.Count);
        Assert.True(selection.Contains(rows[0]));
        Assert.False(selection.Contains(rows[1]));
        Assert.Equal(2, selection.LastIndex(rows));

        selection.Toggle(rows, 2);
        Assert.Equal(0, selection.LastIndex(rows));

        selection.Set(rows, 1);
        Assert.Equal(1, selection.Count);
        selection.Clear();
        Assert.Equal(-1, selection.LastIndex(rows));
    }

    /// <summary>Shift+click selects the range from the anchor; additive keeps the earlier selection.</summary>
    [Fact]
    public void ExtendSelectsTheRangeFromTheAnchor()
    {
        var rows = Enumerable.Range(0, 6).Select(static i => Row(i, $"row {i}")).ToArray();
        var selection = new LogSelection();

        selection.Set(rows, 1);
        selection.Extend(rows, 3, additive: false);
        Assert.Equal("row 1" + Environment.NewLine + "row 2" + Environment.NewLine + "row 3", selection.Text(rows));

        selection.Extend(rows, 0, additive: false);
        Assert.Equal(2, selection.Count);

        selection.Toggle(rows, 5);
        selection.Extend(rows, 4, additive: true);
        Assert.Equal(4, selection.Count);
    }

    /// <summary>Extending with nothing selected anchors on the clicked row.</summary>
    [Fact]
    public void ExtendWithoutAnchorSelectsTheClickedRow()
    {
        var rows = new[] { Row(0, "a"), Row(1, "b") };
        var selection = new LogSelection();

        selection.Extend(rows, 1, additive: false);

        Assert.Equal("b", selection.Text(rows));
    }

    /// <summary>Select all covers every visible row, and copy text keeps whole multi-line entries in list order.</summary>
    [Fact]
    public void SelectAllCopiesEveryWholeEntry()
    {
        var rows = new[] { Row(0, "first\n  at A()"), Row(1, "second") };
        var selection = new LogSelection();

        selection.SelectAll(rows);

        Assert.Equal("first\n  at A()" + Environment.NewLine + "second", selection.Text(rows));
        selection.SelectAll([]);
        Assert.Equal(0, selection.Count);
    }

    /// <summary>Entries hidden by a filter are not part of the copied text.</summary>
    [Fact]
    public void HiddenEntriesAreNotCopied()
    {
        var rows = new[] { Row(0, "a"), Row(1, "b"), Row(2, "c") };
        var selection = new LogSelection();
        selection.SelectAll(rows);

        Assert.Equal("c", selection.Text([rows[2]]));
    }

    /// <summary>Every severity has its own icon id, and trace shares the debug icon.</summary>
    [Theory]
    [InlineData(LogLevel.Critical, "log.error")]
    [InlineData(LogLevel.Error, "log.error")]
    [InlineData(LogLevel.Warning, "log.warning")]
    [InlineData(LogLevel.Information, "log.info")]
    [InlineData(LogLevel.None, "log.info")]
    [InlineData(LogLevel.Debug, "log.debug")]
    [InlineData(LogLevel.Trace, "log.debug")]
    public void SeveritiesMapToIcons(LogLevel level, string icon) => Assert.Equal(icon, OutputPanel.LevelIcon(level));

    /// <summary>Each category counts its own entries, trace joins debug, and hiding Studio drops internal ones.</summary>
    [Fact]
    public void CountsGroupEntriesBySeverity()
    {
        var now = DateTimeOffset.Now;
        LogLine[] lines =
        [
            new(LogLevel.Error, now, "e"), new(LogLevel.Critical, now, "c"), new(LogLevel.Warning, now, "w"),
            new(LogLevel.Information, now, "i"), new(LogLevel.Trace, now, "t"), new(LogLevel.Debug, now, "d"),
            new(LogLevel.Information, now, "studio") { IsInternal = true },
        ];

        Assert.Equal(new LogCounts(2, 1, 2, 2), LogView.Count(lines, studio: true));
        Assert.Equal(new LogCounts(2, 1, 1, 2), LogView.Count(lines, studio: false));
    }

    /// <summary>Counts are exact up to 999 and capped after, so a toggle never grows with the log.</summary>
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "999+")]
    public void CountLabelsAreCapped(int count, string label) => Assert.Equal(label, OutputPanel.CountLabel(count));

    /// <summary>Studio messages are shown by default, and the choice is a stored setting rather than a toolbar box.</summary>
    [Fact]
    public void StudioMessagesAreAStoredSetting()
    {
        Assert.True(new OutputPanelSettings().ShowStudio);
        Assert.DoesNotContain(typeof(OutputPanel).GetFields(instanceFields), static field => field.Name == "showStudio");
    }

    /// <summary>Ctrl+A then Ctrl+C copies every visible entry whole, and turning Studio off hides internal rows.</summary>
    [Fact]
    public void PanelCopiesTheWholeSelectionAndHonoursTheStudioSetting()
    {
        LogBuffer.Clear();
        try
        {
            using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(),
                services, NullLogger.Instance);
            var settings = new OutputPanelSettings { ClearOnRecompile = false };
            var bridge = new OutputLogBridge(settings, play, build);
            var focus = Substitute.For<IFocusTracker>();
            focus.ActivePanelId.Returns(GayaPlugin.OutputPanelId);
            var panel = new OutputPanel(NullLogger.Instance, settings, bridge, new SettingsService(),
                focus, new StudioLocalization());
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var clipboard = "";
            input.When(handler => handler.SetClipboardText(Arg.Any<string>()))
                .Do(call => clipboard = call.Arg<string>());
            var gui = new Gui { Input = input };
            using var surface = SKSurface.Create(new SKImageInfo(1200, 600));
            var font = Font.FromFamilyName("sans-serif", 14);
            void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

            var source = Path.GetTempFileName();
            var traced = $"three\n  at Game.B() in {source}:line 4";
            LogBuffer.Provider.CreateLogger("Game").LogInformation("one\n  at Game.A()");
            LogBuffer.Provider.CreateLogger("Game").LogWarning("two");
            LogBuffer.Provider.CreateLogger("Editor").LogInformation("studio");
            LogBuffer.Provider.CreateLogger("Game").LogError("{Trace}", traced);
            Frame();
            var rows = (IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!.GetValue(panel)!;
            Assert.Equal(4, rows.Count);

            settings.ShowStudio = false;
            Frame();
            rows = (IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!.GetValue(panel)!;
            Assert.Equal(3, rows.Count);

            input.IsKeyDown(GKey.LeftControl).Returns(true);
            input.IsKeyPressed(GKey.A).Returns(true);
            Frame();
            input.IsKeyPressed(GKey.A).Returns(false);
            input.IsKeyPressed(GKey.C).Returns(true);
            Frame();
            Assert.Equal(string.Join(Environment.NewLine, "one\n  at Game.A()", "two", traced), clipboard);

            var links = (IReadOnlyList<LogLink>)typeof(OutputPanel).GetField("detailLinks", instanceFields)!
                .GetValue(panel)!;
            Assert.Equal($"{source}:line 4", traced.Substring(Assert.Single(links).Start, links[0].Length));
            File.Delete(source);
        }
        finally { LogBuffer.Clear(); }
    }
}
