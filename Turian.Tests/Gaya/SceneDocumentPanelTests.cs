namespace Turian.Tests;

/// <summary>Checks the scene tabs' panels: independent tree state, per-tab cameras, the play placeholder and releases.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneDocumentPanelTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(960, 540));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);

    Gui NewGui()
    {
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        return new Gui { Input = input };
    }

    SceneTreePanel TreePanel(SceneTabsHarness harness) => new(harness.Tree, harness.Inspector, harness.Assets, null!,
        null!, harness.Settings, harness.Undo, null!, null!, null!, harness.Database);

    static TreeViewState StateOf(SceneTreePanel panel) => (TreeViewState)typeof(SceneTreePanel)
        .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;

    /// <summary>Each tab's tree keeps its own expansion and selection, and a tab's selection returns when it is active again.</summary>
    [Fact]
    public void TreesKeepExpansionAndSelectionPerTab()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a", "b", "c");
        using var treeOne = TreePanel(harness);
        harness.Inspector.Select(harness.Node(one, "a", "b", "c"));
        var gui = NewGui();
        InspectorFormsRenderingTests.Frame(gui, surface, font, treeOne.Render);
        Assert.False(StateOf(treeOne).IsCollapsed("0/0/0", 3));
        Assert.Equal("0/0/0/0", StateOf(treeOne).SelectedId);

        var two = harness.Open("two", "x", "y", "z");
        using var treeTwo = TreePanel(harness);
        InspectorFormsRenderingTests.Frame(gui, surface, font, treeTwo.Render);
        Assert.True(StateOf(treeTwo).IsCollapsed("0/0/0", 3));
        Assert.Null(StateOf(treeTwo).SelectedId);
        Assert.Empty(harness.Inspector.SelectedNodes);

        harness.Inspector.Select(harness.Node(two, "x"));
        InspectorFormsRenderingTests.Frame(gui, surface, font, treeTwo.Render);
        Assert.Equal("0/0", StateOf(treeTwo).SelectedId);

        harness.Activate(one);
        InspectorFormsRenderingTests.Frame(gui, surface, font, treeOne.Render);
        Assert.False(StateOf(treeOne).IsCollapsed("0/0/0", 3));
        Assert.Equal("0/0/0/0", StateOf(treeOne).SelectedId);
        Assert.Same(harness.Node(one, "a", "b", "c"), harness.Inspector.SelectedNode);
    }

    SceneViewport DeviceLessView(SceneTabsHarness harness, BuildManager build) => new(null!, harness.Database, harness.Tree,
        harness.Inspector, new GizmoDrawerCatalog(build), harness.Play,
        new EditorCameraSettings(), NullLogger.Instance, harness.Undo, new LocaleService());

    /// <summary>A Scene panel whose scene is not loaded names its own tab instead of drawing a viewport, and suspends cleanly.</summary>
    [Fact]
    public void ScenePanelNamesItsTabWhileItsSceneIsNotLoaded()
    {
        using var harness = new SceneTabsHarness();
        using var stage = new PrefabStage(harness.Workspace, harness.Database);
        var tab = new SceneDocument(new AssetWrapper(new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/x.prefab" }));
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var view = DeviceLessView(harness, build);
        using var panel = new ScenePanel(view, harness.Tree, harness.Inspector, tab, stage);
        var gui = NewGui();

        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        Assert.DoesNotContain(Descendants(gui.RootNode!), static node => node.Id.StartsWith("scene/toolbar", StringComparison.Ordinal));
        panel.Suspend();
        Assert.False(view.HasRenderTarget);
    }

    /// <summary>In prefab mode the breadcrumb names the prefab tab and leads back to the scene tab it came from.</summary>
    [Fact]
    public void PrefabTabBreadcrumbReturnsToTheScene()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("scene");
        var lamp = harness.AddPrefab("Lamp");
        using var stage = new PrefabStage(harness.Workspace, harness.Database);
        Assert.True(stage.OpenPrefab(new Node
        {
            PrefabInstance = new PrefabInstance { Source = new AssetReference<Prefab>(lamp) },
        }));
        var prefabTab = (SceneDocument)harness.Documents.Active!;
        Assert.Equal(lamp.ToString(), prefabTab.Id);
        Assert.Equal("turian.scene", prefabTab.Kind);
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var view = DeviceLessView(harness, build);
        using var panel = new ScenePanel(view, harness.Tree, harness.Inspector, prefabTab, stage);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };

        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        var crumb = Descendants(gui.RootNode!).Single(static node => node.Id == "scene/breadcrumb/0");
        input.MousePosition.Returns(crumb.Rect.Center);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        Assert.Same(scene, harness.Documents.Active);
        Assert.Empty(stage.Trail);
    }

    /// <summary>Closing a tab detaches its tree from the scene tree controller.</summary>
    [Fact]
    public void DisposedTreeStopsFollowingTheController()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a");
        var tree = TreePanel(harness);
        var gui = NewGui();
        InspectorFormsRenderingTests.Frame(gui, surface, font, tree.Render);
        var stale = typeof(SceneTreePanel).GetField("stale", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)stale.GetValue(tree)!);

        tree.Dispose();
        harness.Tree.OpenAsset(one.Wrapper.Asset);

        Assert.False((bool)stale.GetValue(tree)!);
    }

    GamePanel GamePanelFor(SceneTabsHarness harness, SceneDocument tab, out GameViewport viewport,
        VulkanFixture? gpu = null)
    {
        viewport = new GameViewport(gpu?.Vulkan!, harness.Database, harness.Tree, harness.Play, NullLogger.Instance);
        return new GamePanel(viewport, tab, harness.Playing, harness.Play, new StudioLocalization());
    }

    /// <summary>While Play runs in one tab, another tab's Game panel only says where the game is playing.</summary>
    [Fact]
    public void OtherTabsGamePanelShowsAPlaceholderWhilePlaying()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        using var gameOne = GamePanelFor(harness, one, out _);
        using var gameTwo = GamePanelFor(harness, two, out _);
        var gui = NewGui();
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameTwo.Render);
        Assert.Null(gameTwo.PlaceholderText);
        Assert.DoesNotContain(Descendants(gui.RootNode!), static node => node.Id == "game/playingElsewhere");

        harness.Activate(one);
        Assert.True(harness.Play.Start());
        harness.Activate(two);

        InspectorFormsRenderingTests.Frame(gui, surface, font, gameTwo.Render);
        Assert.Equal("Playing in one", gameTwo.PlaceholderText);
        Assert.Contains(Descendants(gui.RootNode!), static node => node.Id == "game/playingElsewhere");
        Assert.Null(gameOne.PlaceholderText);
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameOne.Render);
        Assert.DoesNotContain(Descendants(gui.RootNode!), static node => node.Id == "game/playingElsewhere");

        harness.Play.Stop();
        Assert.Null(gameTwo.PlaceholderText);
    }

    /// <summary>Two Scene views keep their own camera, follow framing only when active, and rebuild at the same pose.</summary>
    [Fact]
    public void SceneViewsKeepTheirOwnCameraThroughSuspension()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a");
        var two = harness.Open("two");
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        SceneViewport ViewOf(SceneDocument tab) => new(fixture.Vulkan, harness.Database, harness.Tree,
            harness.Inspector, new GizmoDrawerCatalog(build), harness.Play, new EditorCameraSettings(),
            NullLogger.Instance, harness.Undo, new LocaleService(),
            isActive: () => ReferenceEquals(harness.Documents.Active, tab));
        using var viewOne = ViewOf(one);
        using var viewTwo = ViewOf(two);
        var gui = NewGui();

        harness.Activate(one);
        Frame(gui, viewOne);
        var target = harness.Node(one, "a");
        target.Position = new Vector3(50, 0, 0);
        var untouched = CameraOf(viewOne).Position;
        harness.Tree.RequestFrameNode(target);
        var framed = CameraOf(viewOne).Position;
        Assert.NotEqual(untouched, framed);
        viewOne.ToggleProjection();

        harness.Activate(two);
        Frame(gui, viewTwo);
        Assert.Equal(untouched, CameraOf(viewTwo).Position);
        Assert.False(viewTwo.IsOrthographic);
        harness.Tree.RequestFrameNode(target);
        Assert.NotEqual(untouched, CameraOf(viewTwo).Position);
        Assert.Equal(framed, CameraOf(viewOne).Position);

        var lens = new CameraComponent();
        target.AddComponent(lens);
        harness.Tree.RequestFrameNode(target, new FrameNodeOptions { MatchRotation = true });
        Assert.Equal(lens.Yaw, CameraOf(viewTwo).Yaw, 1e-3f);

        viewOne.Suspend();
        Assert.False(viewOne.HasRenderTarget);
        Assert.True(viewTwo.HasRenderTarget);

        harness.Activate(one);
        Frame(gui, viewOne);
        Assert.True(viewOne.HasRenderTarget);
        Assert.True(Vector3.Distance(framed, CameraOf(viewOne).Position) < 1e-4f);
        Assert.True(viewOne.IsOrthographic);

        viewOne.Dispose();
        Assert.False(viewOne.HasRenderTarget);
        Assert.True(viewTwo.HasRenderTarget);
    }

    /// <summary>The tab that is playing keeps its Game render target until Play ends, then releases what it owed.</summary>
    [Fact]
    public void PlayingTabsGameViewIsNotSuspendedWhilePlaying()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        using var gameOne = GamePanelFor(harness, one, out var viewOne, fixture);
        using var gameTwo = GamePanelFor(harness, two, out var viewTwo, fixture);
        var gui = NewGui();

        harness.Activate(two);
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameTwo.Render);
        harness.Activate(one);
        Assert.True(harness.Play.Start());
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameOne.Render);
        Assert.True(viewOne.HasRenderTarget);

        gameOne.Suspend();
        Assert.True(viewOne.HasRenderTarget);
        gameTwo.Suspend();
        Assert.False(viewTwo.HasRenderTarget);

        harness.Play.Stop();
        Assert.False(viewOne.HasRenderTarget);
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameOne.Render);
        Assert.True(viewOne.HasRenderTarget);
    }

    /// <summary>The Studio's own Scene and Game factories build each tab's panels from the services, bound to that tab.</summary>
    [Fact]
    public void StudioFactoriesBuildThePanelsOfTheirTab()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a");
        var two = harness.Open("two");
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var stage = new PrefabStage(harness.Workspace, harness.Database);
        using var services = new ServiceCollection()
            .AddSingleton(fixture.Vulkan)
            .AddSingleton(harness.Database)
            .AddSingleton(harness.Tree)
            .AddSingleton(harness.Inspector)
            .AddSingleton(new GizmoDrawerCatalog(build))
            .AddSingleton(harness.Play)
            .AddSingleton(new EditorCameraSettings())
            .AddSingleton<ILogger>(NullLogger.Instance)
            .AddSingleton(harness.Undo)
            .AddSingleton(new LocaleService())
            .AddSingleton(stage)
            .AddSingleton<IDocumentService>(harness.Documents)
            .AddSingleton(harness.Playing)
            .AddSingleton(new StudioLocalization())
            .BuildServiceProvider();
        using var frame = new WorkbenchFrame(configure: app =>
        {
            var context = Substitute.For<IPluginContext>();
            context.Panels.Returns(app.Panels);
            context.DocumentKinds.Returns(app.DocumentKinds);
            SceneDocumentPanels.Register(context);
        });
        var sceneFactory = frame.Panels.All.Single(static panel => panel.Id == GayaPlugin.ViewportPanelId).DocumentFactory!;
        var gameFactory = frame.Panels.All.Single(static panel => panel.Id == GayaPlugin.GamePanelId).DocumentFactory!;

        var sceneInstance = sceneFactory(services, one);
        var gameInstance = gameFactory(services, two);

        Assert.IsType<ScenePanel>(sceneInstance);
        Assert.IsType<GamePanel>(gameInstance);
        using var sceneDisposable = (IDisposable)sceneInstance;
        using var gameDisposable = (IDisposable)gameInstance;
        harness.Activate(one);
        var gui = NewGui();
        InspectorFormsRenderingTests.Frame(gui, surface, font, sceneInstance.Render);
        InspectorFormsRenderingTests.Frame(gui, surface, font, gameInstance.Render);
        Assert.Null(((GamePanel)gameInstance).PlaceholderText);
    }

    static EditorCamera CameraOf(SceneViewport viewport) => ((SceneViewerService)typeof(SceneViewport)
        .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!).Camera;

    void Frame(Gui gui, SceneViewport viewport) => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
    {
        using (current.Node(960, 540, "viewport").Enter())
        {
            viewport.Render(current);
        }
    });

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
