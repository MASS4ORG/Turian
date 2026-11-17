namespace Turian.Tests;

/// <summary>Exercises Studio discovery, test execution and source navigation through the real panel.</summary>
[Collection(SerialTests.Name)]
public sealed class TestRunnerPanelTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-test-panel-").FullName;
    readonly SettingsService settings = new();
    readonly BackgroundTaskManager tasks = new() { Retention = TimeSpan.Zero };
    readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    readonly BuildManager build;
    readonly PlayModeService play;
    readonly TestRunnerPanel panel;
    readonly Gui gui;
    readonly IInputHandler input = Substitute.For<IInputHandler>();
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(1000, 800));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);

    /// <summary>Creates a headless workbench with isolated project and background tasks.</summary>
    public TestRunnerPanelTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        var project = new AppSettings { Title = "PanelTests", ProjectAbsoluteDir = root };
        settings.Set(project);
        ProjectPackages.OptOutOfDefault(root, ProjectPackages.RenderBrick);
        build = new BuildManager(project, NullLogger.Instance);
        var host = Substitute.For<IPlaySceneHost>();
        host.CurrentSceneRoot.Returns(new Node());
        play = new PlayModeService(host, new AssetDatabase(), services, NullLogger.Instance);
        var context = Substitute.For<IPluginContext>();
        context.CommandLineArgs.Returns([]);
        var panels = new List<PanelDescriptor>();
        context.Panels.When(registry => registry.Register(Arg.Any<PanelDescriptor>()))
            .Do(call => panels.Add(call.Arg<PanelDescriptor>()));
        var commands = new List<CommandDescriptor>();
        context.Commands.When(registry => registry.Register(Arg.Any<CommandDescriptor>()))
            .Do(call => commands.Add(call.Arg<CommandDescriptor>()));
        new GayaPlugin().Configure(context);
        var shell = Substitute.For<IShellHost>();
        using var panelServices = new ServiceCollection().AddSingleton(settings).AddSingleton(play).AddSingleton(build)
            .AddSingleton(new BackgroundTaskRunner(tasks, NullLogger.Instance)).AddSingleton(shell)
            .AddSingleton<ILogger>(NullLogger.Instance).BuildServiceProvider();
        var descriptor = Assert.Single(panels, static item => item.Id == GayaPlugin.TestRunnerPanelId);
        Assert.False(descriptor.OpenByDefault);
        panel = Assert.IsType<TestRunnerPanel>(descriptor.Factory(panelServices));
        var command = Assert.Single(commands, static item => item.Id == "gaya.turian.tests");
        Assert.True(command.CanExecute!(panelServices));
        command.Execute(panelServices);
        shell.Received().ShowPanel(GayaPlugin.TestRunnerPanelId);
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns(string.Empty);
        gui = new Gui { Input = input };
        Frame();
        Frame();
    }

    void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

    IEnumerable<LayoutNode> Nodes() => Walk(gui.RootNode!);

    static IEnumerable<LayoutNode> Walk(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Walk));

    void Click(string id)
    {
        input.MousePosition.Returns(Nodes().Single(node => node.Id == id).Rect.Center);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame();
    }

    SceneTestReport Report => (SceneTestReport)typeof(TestRunnerPanel)
        .GetField("report", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;

    async Task<BackgroundTaskStatus> Execute(string action)
    {
        var last = tasks.Snapshot().Select(static item => item.Id).DefaultIfEmpty().Max();
        var finished = new TaskCompletionSource<BackgroundTaskStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (tasks.Snapshot().FirstOrDefault(item => item.Id > last && item.IsFinished) is { } task)
            {
                finished.TrySetResult(task.Status);
            }
        }
        tasks.Changed += Changed;
        try
        {
            Click(action);
            return await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally { tasks.Changed -= Changed; }
    }

    /// <summary>Studio lists tests without running them, then shows a failure and its actual source line.</summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task DiscoversRunsAndShowsFailingSource()
    {
        await new ProjectBootstrapper().CreateAsync(root);
        ProjectPackages.OptOutOfDefault(root, ProjectPackages.RenderBrick);
        Assert.Equal(BackgroundTaskStatus.Completed, await Execute("tests/discover"));
        Frame();
        Frame();
        Assert.Equal(SceneTestStatus.NotRun, Assert.Single(Report.Tests).Status);
        var source = Path.Combine(root, "Tests", "SceneTests.cs");
        await File.WriteAllTextAsync(source, File.ReadAllText(source).Replace("Equal(3L", "Equal(4L", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        Assert.Equal(BackgroundTaskStatus.Failed, await Execute("tests/run"));
        Frame();
        Frame();
        var test = Assert.Single(Report.Tests);
        Assert.Equal(SceneTestStatus.Failed, test.Status);
        Assert.Equal(source, test.SourceFile);
        Assert.True(test.SourceLine > 0);
        Assert.Contains(Nodes(), static node => node.Id == "tests/location");
        Assert.Contains(Nodes(), static node => node.Id == "tests/source/0");
        File.Delete(source);
        Click("tests/openSource");
        Click("tests/row/0");
        settings.Set(new AppSettings { ProjectAbsoluteDir = Path.Combine(root, "other") });
        Frame();
        Assert.Empty(Report.Tests);
    }

    /// <summary>An empty project starts with runnable discovery and no stale test rows.</summary>
    [Fact]
    public void EmptyProjectsHaveNoResults()
    {
        Assert.Empty(Report.Tests);
        Assert.Contains(Nodes(), static node => node.Id == "tests/discover");
        Assert.Contains(Nodes(), static node => node.Id == "tests/run");
    }

    /// <summary>Stops test services and removes the project fixture.</summary>
    public void Dispose()
    {
        play.Stop();
        build.Dispose();
        services.Dispose();
        surface.Dispose();
        font.Dispose();
        PrecastBrickFixture.DeleteTree(root);
    }
}
