namespace Turian.Tests;

/// <summary>Checks that statistics are excluded from editor Scene rendering.</summary>
[Collection(SerialTests.Name)]
public sealed class FrameStatisticsHudTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>The Scene viewer excludes statistics and permits camera rotation through the upper-left region.</summary>
    [Fact]
    public void SceneViewDoesNotInjectStatisticsOrBlockCameraRotation()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var settings = new EditorCameraSettings();
        using var viewport = new SceneViewport(fixture.Vulkan, database, tree, inspector,
            new GizmoDrawerCatalog(build), play,
            settings, NullLogger.Instance, undo, new LocaleService());
        var toolbar = new SceneToolbar(viewport, () => { });
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(800, 450));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        Assert.DoesNotContain(Descendants(gui.RootNode!), child => child.Id == "scene/statistics");
        var viewer = (SceneViewerService)typeof(SceneViewport)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
        Assert.False(viewer.CollectStatistics);
        var position = new Vector2(200, 150);
        var before = viewer.Camera.Orientation;
        input.MousePosition.Returns(position);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Right).Returns(true);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
        input.MousePosition.Returns(position + new Vector2(20, 10));
        Frame();
        Assert.NotEqual(before, viewer.Camera.Orientation);
        input.IsMouseButtonDown(GMouseButton.Right).Returns(false);
        Frame();
        Assert.DoesNotContain(Descendants(gui.RootNode!), child => child.Id.Contains("statistics"));
        Assert.False(viewer.CollectStatistics);
        Assert.Equal(default, viewer.FrameStats);

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
        {
            toolbar.Render(current);
            using (current.Node(800, 400, "viewport").Enter()) viewport.Render(current);
        });
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
