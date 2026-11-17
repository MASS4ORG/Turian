namespace Turian.Tests;

/// <summary>Covers the status bar's readout, its latest-message item, the task summary and the Background Tasks panel.</summary>
[Collection(SerialTests.Name)]
public sealed class StatusBarTests
{
    const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The readout shows rate, frame time and memory in one line.</summary>
    [Fact]
    public void ReadoutShowsRateFrameTimeAndMemory() =>
        Assert.Equal("60 fps  ·  16.7 ms  ·  1.5 GB  ·  Gaya", StatusReadout.Format(60f, 3L << 29));

    /// <summary>Frame time is the inverse of the rate and zero before anything is measured.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(100f, 10f)]
    [InlineData(50f, 20f)]
    public void FrameTimeInvertsTheRate(float fps, float milliseconds) =>
        Assert.Equal(milliseconds, StatusReadout.FrameMilliseconds(fps), 3);

    /// <summary>Sizes read in megabytes below a gigabyte and in gigabytes with a decimal above it.</summary>
    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(512L << 20, "512 MB")]
    [InlineData(1L << 30, "1.0 GB")]
    [InlineData(5L << 29, "2.5 GB")]
    public void SizesUseReadableUnits(long bytes, string text) => Assert.Equal(text, StatusReadout.Bytes(bytes));

    /// <summary>Clicking the latest message shows the Output panel and hands it that entry.</summary>
    [Fact]
    public void ClickingTheLatestMessageFocusesOutputOnIt()
    {
        LogBuffer.Clear();
        try
        {
            using var frame = new WorkbenchFrame();
            var shell = Substitute.For<IShellHost>();
            var requests = new OutputRevealService();
            frame.Chrome.Register(new ChromeDescriptor("lastLog", ChromeSlot.StatusBar,
                _ => new LastLogChrome(requests, shell), TaskBarChrome.Height, Weight: 4f));
            LogBuffer.Provider.CreateLogger("Game").LogWarning("first\nsecond line");
            frame.Draw();
            var item = WorkbenchFrame.Descendants(frame.Gui.RootNode!).Single(node => node.Id == "statusbar/lastLog");

            frame.Input.MousePosition.Returns(item.Rect.Center);
            frame.Draw();
            frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            frame.Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            frame.Draw();
            frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            frame.Input.IsMouseButtonDown(GMouseButton.Left).Returns(false);

            shell.Received().ShowPanel(GayaPlugin.OutputPanelId);
            Assert.Equal("first\nsecond line", requests.Take()?.Text);
            Assert.Null(requests.Take());
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>The Output panel makes a requested entry visible, selecting it even when its severity was hidden.</summary>
    [Fact]
    public void OutputSelectsARequestedHiddenEntry()
    {
        LogBuffer.Clear();
        try
        {
            using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(),
                services, NullLogger.Instance);
            var settings = new OutputPanelSettings { ClearOnRecompile = false };
            var focus = Substitute.For<IFocusTracker>();
            var requests = new OutputRevealService();
            var panel = new OutputPanel(NullLogger.Instance, settings, new OutputLogBridge(settings, play, build),
                new SettingsService(), focus, new StudioLocalization(), requests);
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var gui = new Gui { Input = input };
            using var surface = SKSurface.Create(new SKImageInfo(1200, 600));
            var font = Font.FromFamilyName("sans-serif", 14);
            var logger = LogBuffer.Provider.CreateLogger("Game");
            logger.LogInformation("one");
            logger.LogDebug("hidden debug");
            logger.LogInformation("three");
            InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
            Assert.Equal(2, ((IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!
                .GetValue(panel)!).Count);

            requests.Request(LogBuffer.Snapshot()[1]);
            InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

            var rows = (IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!.GetValue(panel)!;
            var selected = (int)typeof(OutputPanel).GetField("selectedIndex", instanceFields)!.GetValue(panel)!;
            Assert.Equal(3, rows.Count);
            Assert.Equal("hidden debug", rows[selected].Line.Text);
        }
        finally { LogBuffer.Clear(); }
    }

    /// <summary>Clicking the task summary shows the Background Tasks panel instead of opening a popup.</summary>
    [Fact]
    public void ClickingTheTaskSummaryShowsThePanel()
    {
        using var frame = new WorkbenchFrame();
        var shell = Substitute.For<IShellHost>();
        var tasks = new BackgroundTaskManager();
        var chrome = new TaskBarChrome(tasks, shell);
        frame.Chrome.Register(new ChromeDescriptor("tasks", ChromeSlot.StatusBar, _ => chrome,
            Height: TaskBarChrome.Height));
        var id = tasks.Begin(BackgroundTaskKind.Import, "Importing assets");
        tasks.SetProgress(id, 0.5f);
        frame.Draw();
        var row = WorkbenchFrame.Descendants(frame.Gui.RootNode!).First(node => node.Id == "chrome/StatusBar/tasks");

        frame.Input.MousePosition.Returns(new Vector2(row.Rect.X + 20, row.Rect.Y + row.Rect.H / 2));
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        frame.Draw();

        shell.Received().ShowPanel(GayaPlugin.TasksPanelId);
    }

    /// <summary>The panel lists each root task with its row, and the Cancel button asks the task to stop.</summary>
    [Fact]
    public void TasksPanelListsAndCancelsTasks()
    {
        var tasks = new BackgroundTaskManager();
        var panel = new BackgroundTasksPanel(tasks);
        var id = tasks.Begin(BackgroundTaskKind.Import, "Importing assets");
        tasks.SetProgress(id, 0.25f);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        var nodes = WorkbenchFrame.Descendants(gui.RootNode!).ToList();
        Assert.Contains(nodes, node => node.Id == $"tasks/{id}");

        var cancel = nodes.Single(node => node.Id == $"tasks/{id}/cancel");
        input.MousePosition.Returns(cancel.Rect.Center);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        Assert.True(Assert.Single(tasks.Snapshot()).CancelRequested);
    }

    /// <summary>With nothing running the panel says so rather than staying blank.</summary>
    [Fact]
    public void EmptyTasksPanelSaysSo()
    {
        var panel = new BackgroundTasksPanel(new BackgroundTaskManager());
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(500, 300));
        var font = Font.FromFamilyName("sans-serif", 14);

        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        Assert.Contains(WorkbenchFrame.Descendants(gui.RootNode!), static node => node.Id == "tasks/list");
    }
}
