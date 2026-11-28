namespace Turian.Tests;

/// <summary>Exercises build-profile authoring through the panel's real forms and menus.</summary>
[Collection(SerialTests.Name)]
public sealed class BuildProfilesPanelTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-profile-panel-").FullName;
    readonly SettingsService settings = new();
    readonly AssetImporter importer;
    readonly BuildManager build;
    readonly UndoService undo;
    readonly AssetAutoSave autoSave;
    readonly BuildProfilesPanel panel;
    readonly AssetWorkspace documents;
    readonly BackgroundTaskManager tasks = new();
    readonly Gui gui;
    readonly IInputHandler input = Substitute.For<IInputHandler>();
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(1000, 800));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);

    /// <summary>Creates an isolated project with a real importer, inspector, autosave and undo history.</summary>
    public BuildProfilesPanelTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        var project = new AppSettings { Title = "Profiles", ProjectAbsoluteDir = root };
        settings.Set(project);
        ProjectSettingsFiles.CreateProjectAsset(project, [], []);
        ProjectPackages.OptOutOfDefault(root, ProjectPackages.RenderBrick);
        build = new BuildManager(project, NullLogger.Instance);
        var database = new AssetDatabase();
        importer = new AssetImporter(NullLogger.Instance, database, settings, build);
        importer.StopMonitoring();
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<DataAsset>(Arg.Any<Guid>()).Returns(call =>
            Task.FromResult(new AssetSourceIndex(root).Load(call.Arg<Guid>())));
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var tree = new SceneTreeController(assets, settings, importer, loader,
            Substitute.For<ISceneManager>(), database);
        undo = new UndoService(tree, selection, assets, loader);
        var inspections = new AssetInspectionService(importer, new AssetTypeCatalog(), settings,
            loader, NullLogger.Instance);
        autoSave = new AssetAutoSave(inspections, TimeSpan.FromHours(1));
        documents = new AssetWorkspace(assets, settings, [new FakeDocumentKind()]);
        panel = new BuildProfilesPanel(settings, inspections, autoSave, undo,
            new ReferenceDrawer(new ReferencePicker(database, tree, loader), selection, new AssetRevealService()),
            build, new BackgroundTaskRunner(tasks, NullLogger.Instance), new UnsavedWork(documents, autoSave));
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns(string.Empty);
        gui = new Gui { Input = input };
        Frame();
        Frame();
    }

    void Frame(Action<Gui>? render = null)
    {
        surface.Canvas.Clear(new SKColor(24, 25, 29));
        InspectorFormsRenderingTests.Frame(gui, surface, font, render ?? panel.Render);
    }

    IEnumerable<LayoutNode> Nodes() => Walk(gui.RootNode!);

    static IEnumerable<LayoutNode> Walk(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Walk));

    void Click(LayoutNode node, Action<Gui>? render = null)
    {
        input.MousePosition.Returns(node.Rect.Center);
        Frame(render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame(render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame(render);
        Frame(render);
    }

    void AddClient() => AddTarget(0);

    /// <summary>Device profiles expose a separate runnable action and both build buttons defer work until after rendering.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedProfileOffersBuildAndDeviceRunActions(bool deploys)
    {
        ProjectSettingsFiles.CreateBuildProfile(settings.Settings!,
            new BuildProfile { Name = "Player", Platform = "missing-device" });
        var listing = Assert.Single(BuildProfiles.List(settings.Settings!));
        var platform = Substitute.For<IBuildPlatform>();
        platform.Deploys.Returns(deploys);
        void RenderActions(Gui context) => panel.Actions(context, listing, platform);
        Frame(RenderActions);
        Frame(RenderActions);

        Assert.Equal(deploys, Nodes().Any(static node => node.Id == "buildProfiles/run"));
        Click(Nodes().Single(static node => node.Id == "buildProfiles/build"), RenderActions);
        var pending = typeof(BuildProfilesPanel).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(pending.GetValue(panel));
        if (deploys)
        {
            pending.SetValue(panel, null);
            Click(Nodes().Single(static node => node.Id == "buildProfiles/run"), RenderActions);
            Assert.NotNull(pending.GetValue(panel));
        }
        var completed = new TaskCompletionSource<BuildTaskStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        build.TaskRunner.TaskCompleted += status => completed.TrySetResult(status);
        ((Action)pending.GetValue(panel)!)();
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(BuildTaskState.Failed, result.State);
        Assert.Contains("missing-device", result.Message);
        Assert.False(Assert.Single(BuildProfiles.List(settings.Settings!)).IsActive);
    }

    /// <summary>Building writes the open documents first, and builds nothing while one cannot be written.</summary>
    [Fact]
    public void BuildingIsRefusedWhileADocumentCannotBeWritten()
    {
        ProjectSettingsFiles.CreateBuildProfile(settings.Settings!, new BuildProfile { Name = "Player" });
        var listing = Assert.Single(BuildProfiles.List(settings.Settings!));
        var source = Path.Combine(root, "Assets", "menu.fake");
        File.WriteAllText(source, string.Empty);
        File.WriteAllText(source + ".meta", Serializer.Serialize(
            new Asset { Id = Guid.NewGuid(), RelativePath = "Assets/menu.fake" }));
        var document = (FakeDocument)documents.Open(source)!.Document!;
        document.Edit();
        document.SaveFailure = "locked";

        typeof(BuildProfilesPanel).GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [listing.Id, false]);

        Assert.Equal([false], document.Saves);
        Assert.Empty(tasks.Snapshot());
    }

    void AddTarget(int index)
    {
        Click(Nodes().Single(static node => node.Id == "buildProfiles/add"));
        var item = Nodes().Single(node => node.Id.StartsWith("/menubar/", StringComparison.Ordinal)
            && node.Id.EndsWith($"/i{index}", StringComparison.Ordinal));
        Click(item);
    }

    void ChooseDropdown(string id, int index)
    {
        var container = Nodes().Single(node => node.Id == id);
        var button = Walk(container).Single(static node => node.Id.EndsWith("/button", StringComparison.Ordinal));
        var prefix = button.Id[..^"/button".Length];
        Click(button);
        Click(Nodes().Single(node => node.Id == $"{prefix}/list/{index}"));
    }

    /// <summary>Editing and saving an existing profile does not prevent the next client from being created.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAClientAfterEditingKeepsBothProfiles(bool saveBeforeAdding)
    {
        AddClient();
        var first = Assert.Single(BuildProfiles.List(settings.Settings!));
        var model = (FormModel)typeof(BuildProfilesPanel)
            .GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var name = model.Sections.SelectMany((section, s) => section.BodyFields.Select((field, f) => (field, s, f)))
            .Single(static entry => entry.field.Name == nameof(BuildProfile.Name));
        Click(Nodes().Single(node => node.Id == $"buildProfiles/{first.Id}/section{name.s}/field{name.f}/text"));
        input.IsKeyDown(GKey.LeftControl).Returns(true);
        input.IsKeyPressed(GKey.A).Returns(true);
        Frame();
        input.IsKeyDown(GKey.LeftControl).Returns(false);
        input.IsKeyPressed(GKey.A).Returns(false);
        input.GetTypedCharacters().Returns("Renamed");
        Frame();
        input.GetTypedCharacters().Returns(string.Empty);
        Frame();
        Assert.True(autoSave.HasPending);
        if (saveBeforeAdding) { autoSave.Flush(force: true); }
        Frame();

        AddClient();
        autoSave.Flush(force: true);
        Frame();
        var listed = BuildProfiles.List(settings.Settings!);

        Assert.Equal(2, listed.Count);
        Assert.Equal(first.Id, listed[0].Id);
        Assert.Equal(new[] { "Renamed", "Client" }, listed.Select(static profile => profile.Profile.Name));
        Assert.Equal(2, Nodes().Count(static node => node.Id.StartsWith("buildProfiles/row/", StringComparison.Ordinal)
            && node.Id.Split('/').Length == 3));
    }

    /// <summary>An installed mobile platform contributes a working Client menu entry with its own defaults.</summary>
    [Fact]
    public void PlatformMenuCreatesAnAndroidClient()
    {
        var platform = Substitute.For<IBuildPlatform>();
        platform.Id.Returns("android");
        platform.DisplayName.Returns("Android");
        platform.Targets.Returns([BuildTarget.Client]);
        platform.RuntimeIdentifiers.Returns(["android-arm64", "android-x64"]);
        var open = true;
        void RenderMenu(Gui context) => context.CascadeMenu(ref open, new Vector2(20, 20),
            menu => panel.PopulateAddMenu(menu, settings.Settings!, [platform]), id: "test-platform-menu");
        Frame(RenderMenu);
        Frame(RenderMenu);
        var item = Assert.Single(Nodes(), static node => node.Id.StartsWith("/menubar/", StringComparison.Ordinal)
            && node.Id.EndsWith("/i0", StringComparison.Ordinal));
        Assert.DoesNotContain(Nodes(), static node => node.Id.EndsWith("/i1", StringComparison.Ordinal));

        Click(item, RenderMenu);
        Frame();

        var created = Assert.Single(BuildProfiles.List(settings.Settings!)).Profile;
        Assert.Equal("Android Client", created.Name);
        Assert.Equal("android", created.Profile.Platform);
        Assert.Equal("android-arm64", created.Profile.RuntimeIdentifier);
        Assert.Equal(BuildTarget.Client, created.Profile.Target);
    }

    /// <summary>The remove action updates the list, and undo restores the profile registration.</summary>
    [Fact]
    public void RemoveProfileCanBeUndone()
    {
        AddClient();
        var first = Assert.Single(BuildProfiles.List(settings.Settings!));
        AddClient();
        Click(Nodes().Single(static node => node.Id == "buildProfiles/remove"));

        Assert.Equal(first.Id, Assert.Single(BuildProfiles.List(settings.Settings!)).Id);
        Assert.True(undo.CanUndo);
        undo.Undo();
        Frame();
        Assert.Equal(2, BuildProfiles.List(settings.Settings!).Count);
        undo.Redo();
        Frame();
        Assert.Equal(first.Id, Assert.Single(BuildProfiles.List(settings.Settings!)).Id);
    }

    /// <summary>A desktop server can be added, assigned a supported RID and switched active through the panel.</summary>
    [Fact]
    public void DesktopServerAndRuntimeCanBeSelected()
    {
        AddClient();
        AddTarget(1);
        var server = BuildProfiles.List(settings.Settings!).Single(static profile => profile.Profile.Profile.Target == BuildTarget.Server);
        ChooseDropdown("buildProfiles/runtime", 3);
        autoSave.Flush(force: true);
        Click(Nodes().Single(static node => node.Id == "buildProfiles/switch"));

        var active = BuildProfiles.Active(new BuildAppSettings { ProjectAbsoluteDir = root })!;
        Assert.Equal(server.Id, active.Profile.Id);
        Assert.Equal(BuildTarget.Server, active.Profile.Target);
        Assert.Equal("linux-x64", active.Profile.RuntimeIdentifier);
        Assert.Contains(Nodes(), static node => node.Id == "buildProfiles/toolchain-status");
        if (Environment.GetEnvironmentVariable("TURIAN_TEST_DUMP") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            Frame();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, "build-profiles.png"));
            data.SaveTo(file);
        }
    }

    /// <summary>Changing an unavailable platform repairs unsupported targets and RIDs while preserving identity.</summary>
    [Fact]
    public void ChangingPlatformUsesSupportedChoices()
    {
        ProjectSettingsFiles.CreateBuildProfile(settings.Settings!, new BuildProfile
        {
            Name = "Moved platform",
            Platform = "unavailable-mobile",
            Target = (BuildTarget)99,
            RuntimeIdentifier = "android-arm64",
        });
        Frame();
        Frame();
        var original = Assert.Single(BuildProfiles.List(settings.Settings!));

        ChooseDropdown("buildProfiles/platform", 0);
        autoSave.Flush(force: true);
        var changed = Assert.Single(BuildProfiles.List(settings.Settings!));
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal("desktop", changed.Profile.Profile.Platform);
        Assert.Equal(BuildTarget.Client, changed.Profile.Profile.Target);
        Assert.Null(changed.Profile.Profile.RuntimeIdentifier);

        ChooseDropdown("buildProfiles/target", 1);
        autoSave.Flush(force: true);
        Assert.Equal(BuildTarget.Server, Assert.Single(BuildProfiles.List(settings.Settings!)).Profile.Profile.Target);
    }

    /// <summary>Slow and failed platform probes remain readable without blocking the form.</summary>
    [Fact]
    public void ToolchainStatusHandlesPendingReadyAndFailedChecks()
    {
        Assert.Null(BuildProfilesPanel.CompletedToolchainStatus(null));
        var pending = new TaskCompletionSource<BuildPlatformStatus>();
        Assert.Null(BuildProfilesPanel.CompletedToolchainStatus(pending.Task));
        pending.SetResult(new(true, "SDK ready"));
        Assert.True(BuildProfilesPanel.CompletedToolchainStatus(pending.Task)!.IsReady);
        var failed = Task.FromException<BuildPlatformStatus>(new IOException("SDK metadata unavailable"));
        var status = BuildProfilesPanel.CompletedToolchainStatus(failed)!;
        Assert.False(status.IsReady);
        Assert.Equal("SDK metadata unavailable", status.Message);
    }

    /// <summary>A user can retry the platform check while continuing profile authoring.</summary>
    [Fact]
    public void ToolchainCanBeCheckedAgain()
    {
        AddClient();
        Click(Nodes().Single(static node => node.Id == "buildProfiles/check-tools"));
        Assert.Contains(Nodes(), static node => node.Id == "buildProfiles/toolchain-status");
        Assert.Single(BuildProfiles.List(settings.Settings!));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        panel.Dispose();
        documents.Dispose();
        undo.Dispose();
        importer.Dispose();
        build.Dispose();
        font.Dispose();
        surface.Dispose();
        Directory.Delete(root, recursive: true);
    }
}
