namespace Turian.Tests;

/// <summary>
/// Checks that Save, Save All, Play, Build and Run and Export reach every workspace document, and that nothing starts
/// while one cannot be written.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class StudioCommandsRoutingTests : IDisposable
{
    readonly DocumentProject project = new();
    readonly BackgroundTaskManager tasks = new();
    readonly BuildManager build = new(new AppSettings(), NullLogger.Instance);
    readonly IPlaySceneHost playHost = Substitute.For<IPlaySceneHost>();
    readonly IShellHost shell = Substitute.For<IShellHost>();
    readonly AssetDatabase database = new();
    readonly ServiceProvider services;
    readonly IPluginContext context = Substitute.For<IPluginContext>();

    /// <summary>Registers the studio commands over a workspace with a contributed document kind.</summary>
    public StudioCommandsRoutingTests()
    {
        playHost.CurrentSceneRoot.Returns(new Node());
        var autoSave = new AssetAutoSave(new AssetInspectionService(null!, new AssetTypeCatalog(), project.Settings,
            null!, NullLogger.Instance));
        services = new ServiceCollection()
            .AddSingleton(project.Workspace)
            .AddSingleton(new UnsavedWork(project.Workspace, autoSave, project.Notifications))
            .AddSingleton(new BackgroundTaskRunner(tasks, NullLogger.Instance))
            .AddSingleton(build)
            .AddSingleton(shell)
            .AddSingleton(Substitute.For<IFocusTracker>())
            .AddSingleton(sp => new PlayModeService(playHost, database, sp, NullLogger.Instance))
            .AddSingleton(sp => new ProjectSession(sp, NullLogger.Instance))
            .AddSingleton(database)
            .AddSingleton(new SceneTreeController(project.Assets, project.Settings, null!,
                Substitute.For<IAssetLoader>(), Substitute.For<ISceneManager>(), database))
            .AddSingleton<ILogger>(NullLogger.Instance)
            .AddSingleton(new StudioLocalization())
            .BuildServiceProvider();
        StudioCommands.Register(context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        services.GetRequiredService<PlayModeService>().Stop();
        services.Dispose();
        build.Dispose();
        project.Dispose();
    }

    CommandDescriptor Command(string id) => context.Commands.ReceivedCalls()
        .Where(static call => call.GetMethodInfo().Name == nameof(ICommandRegistry.Register))
        .Select(static call => (CommandDescriptor)call.GetArguments()[0]!).Last(command => command.Id == id);

    bool CanRun(string id) => Command(id).CanExecute!(services);

    void Run(string id) => Command(id).Execute(services);

    PlayModeService Play => services.GetRequiredService<PlayModeService>();

    // A running task of the same name makes the command's own task a duplicate, so no real build starts.
    TaskCompletionSource Occupy(string label)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec { Label = label, Kind = BackgroundTaskKind.Build },
            (_, _) => release.Task);
        return release;
    }

    /// <summary>Save writes the active document only, and is offered only while one is open.</summary>
    [Fact]
    public void SaveWritesTheActiveDocument()
    {
        Assert.False(CanRun("gaya.turian.save"));
        Run("gaya.turian.save");
        var (_, other) = project.OpenFake("other");
        var (_, active) = project.OpenFake("active");
        other.Edit();
        active.Edit();

        Assert.True(CanRun("gaya.turian.save"));
        Run("gaya.turian.save");

        Assert.False(active.IsDirty);
        Assert.True(other.IsDirty);
        Assert.Empty(other.Saves);
    }

    /// <summary>Save All writes every dirty document, and is offered only while one is open.</summary>
    [Fact]
    public void SaveAllWritesEveryDirtyDocument()
    {
        Assert.False(CanRun("gaya.turian.saveAll"));
        var (_, first) = project.OpenFake("first");
        var (_, second) = project.OpenFake("second");
        first.Edit();
        second.Edit();

        Assert.True(CanRun("gaya.turian.saveAll"));
        Run("gaya.turian.saveAll");

        Assert.False(project.Workspace.HasUnsavedChanges);
    }

    /// <summary>Play saves the contributed documents, starts, and stops on the next call without saving again.</summary>
    [Fact]
    public void PlaySavesTheDocumentsFirst()
    {
        var (_, document) = project.OpenFake("menu");
        document.Edit();

        Run("gaya.turian.play");

        Assert.Equal([false], document.Saves);
        Assert.True(Play.IsActive);
        document.Edit();
        Run("gaya.turian.play");
        Assert.False(Play.IsActive);
        Assert.Single(document.Saves);
    }

    /// <summary>Play reports a scene it cannot start.</summary>
    [Fact]
    public void PlayWithoutASceneDoesNotStart()
    {
        playHost.CurrentSceneRoot.Returns((Node?)null);

        Run("gaya.turian.play");

        Assert.False(Play.IsActive);
    }

    /// <summary>Nothing starts while a document cannot be written, and the user is told which one.</summary>
    [Theory]
    [InlineData("gaya.turian.play", "Cannot play")]
    [InlineData("gaya.turian.playStartupScene", "Cannot play")]
    [InlineData("gaya.turian.buildAndRun", "Cannot build and run")]
    [InlineData("gaya.turian.export", "Cannot export")]
    public void AFailedSaveStopsTheCommand(string command, string refusal)
    {
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        document.SaveFailure = "locked";

        Run(command);

        Assert.True(tab.IsDirty);
        Assert.False(Play.IsActive);
        Assert.Empty(tasks.Snapshot());
        var messages = project.Reported();
        Assert.Contains(messages, static message => message.Contains("locked", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith(refusal, StringComparison.Ordinal)
            && message.Contains("menu", StringComparison.Ordinal));
    }

    /// <summary>A document that says it was written but stays dirty refuses the run too.</summary>
    [Fact]
    public void ADocumentLeftDirtyStopsPlay()
    {
        var (_, document) = project.OpenFake("menu");
        document.Edit();
        document.StaysDirty = true;

        Run("gaya.turian.play");

        Assert.False(Play.IsActive);
        Assert.Contains("Cannot play: these documents are not saved: menu.", project.Reported());
    }

    /// <summary>Build and Run and Export save the contributed documents before their task is submitted.</summary>
    [Theory]
    [InlineData("gaya.turian.buildAndRun", "Build & run")]
    [InlineData("gaya.turian.export", "Export")]
    public void BuildsSaveTheDocumentsFirst(string command, string task)
    {
        var release = Occupy(task);
        var (_, document) = project.OpenFake("menu");
        document.Edit();
        try
        {
            Run(command);

            Assert.Equal([false], document.Saves);
            Assert.False(document.IsDirty);
            Assert.Single(tasks.Snapshot());
        }
        finally
        {
            release.SetResult();
        }
    }

    /// <summary>Play Startup Scene saves the documents, opens the scene and starts it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayStartupSceneSavesTheDocumentsFirst(bool hasProject)
    {
        var (_, document) = project.OpenFake("menu");
        document.Edit();
        var session = services.GetRequiredService<ProjectSession>();
        if (hasProject)
        {
            typeof(ProjectSession).GetProperty(nameof(ProjectSession.Settings))!
                .SetValue(session, project.Settings.Settings);
            var (_, scene) = project.Source("level.prefab");
            database.MergeDatabase(new Dictionary<Guid, AssetRecord>
            {
                [scene.Id] = new()
                {
                    AssetId = scene.Id,
                    AssetTypeName = typeof(Prefab).FullName!,
                    ProjectRootPath = project.Root,
                    SourceRelativePath = scene.RelativePath,
                },
            });
        }

        Run("gaya.turian.playStartupScene");

        Assert.Equal([false], document.Saves);
        Assert.Equal(hasProject, Play.IsActive);
        Run("gaya.turian.playStartupScene");
        Assert.Single(document.Saves);
    }
}
