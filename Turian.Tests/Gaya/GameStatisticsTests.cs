namespace Turian.Tests;

/// <summary>Checks authored and panel statistics in actively playing sessions.</summary>
[Collection(SerialTests.Name)]
public sealed class GameStatisticsTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Preview, pause, hiding and stop disable instrumentation; Play renders the authored component.</summary>
    [Fact]
    public void StatisticsRequireAuthoredComponentAndPlayingSession()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, Substitute.For<IAssetLoader>(),
            Substitute.For<ISceneManager>(), database);
        var root = new Node();
        root.AddComponent(new CameraComponent());
        var authored = new FrameStatisticsHudComponent();
        root.AddComponent(authored);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(tree, database, services, NullLogger.Instance);
        using var viewport = new GameViewport(fixture.Vulkan, database, tree, play, NullLogger.Instance);
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(450, 180));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        var viewer = (SceneViewerService)typeof(GameViewport)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
        Assert.False(viewer.CollectStatistics);
        Assert.Equal(0, authored.Statistics.Count);
        Assert.True(play.Start());
        play.Tick(0.016);
        var runtime = play.PlayRoot!.GetComponent<FrameStatisticsHudComponent>()!;
        Frame();
        Frame();
        Frame();
        Assert.True(viewer.CollectStatistics);
        Assert.True(runtime.Statistics.Count > 0);
        Assert.True(runtime.Statistics.CpuMilliseconds > 0);
        Assert.Equal(0, authored.Statistics.Count);
        play.Pause();
        Frame();
        Assert.False(viewer.CollectStatistics);
        Assert.Equal(0, runtime.Statistics.Count);
        play.Resume();
        Frame();
        Frame();
        Assert.True(runtime.Statistics.Count > 0);
        runtime.ShowHud = false;
        Frame();
        Assert.False(viewer.CollectStatistics);
        runtime.ShowHud = true;
        Frame();
        Assert.True(viewer.CollectStatistics);
        play.Stop();
        Frame();
        Assert.False(viewer.CollectStatistics);

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
    }

    /// <summary>The header opts into a passive, Play-only overlay without adding any scene components.</summary>
    [Fact]
    public void HeaderEnablesStatisticsWithoutAnAuthoredHud()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var database = new AssetDatabase();
        var tree = new SceneTreeController(new AssetManager(), new SettingsService(), null!,
            Substitute.For<IAssetLoader>(), Substitute.For<ISceneManager>(), database);
        var root = new Node();
        root.AddComponent(new CameraComponent());
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(tree, database, services, NullLogger.Instance);
        using var viewport = new GameViewport(fixture.Vulkan, database, tree, play, NullLogger.Instance);
        using var panel = new GamePanel(viewport);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(500, 210));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        var viewer = (SceneViewerService)typeof(GameViewport)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
        var statistics = (FrameStatistics)typeof(GameViewport)
            .GetField("panelStatistics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
        Assert.False(viewport.ShowStatistics);
        Assert.False(viewer.CollectStatistics);
        ClickStats();
        Assert.True(viewport.ShowStatistics);
        Assert.False(viewer.CollectStatistics);
        Assert.Null(Find("game/statistics"));
        Assert.True(play.Start());
        play.Tick(0.016);
        Frame();
        Frame();
        Assert.True(viewer.CollectStatistics);
        Assert.True(statistics.Count > 0);
        var overlay = Assert.IsType<LayoutNode>(Find("game/statistics"));
        Assert.False(overlay.Style.BlocksInput);
        Assert.False((bool)typeof(LayoutNode).GetProperty("IsHitTestVisible",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!);
        var drawing = Assert.IsType<GameStatisticsDrawing>(typeof(GameViewport)
            .GetField("statisticsDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport));
        Assert.NotNull(drawing.Paint);
        for (var i = 0; i < 100; i++) drawing.Render(gui, overlay, surface.Canvas);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) drawing.Render(gui, overlay, surface.Canvas);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Single(play.PlayRoot!.Components);
        Assert.Empty(play.PlayRoot.Children);
        ClickStats();
        Assert.False(viewer.CollectStatistics);
        Assert.Equal(0, statistics.Count);
        Assert.Null(Find("game/statistics"));
        viewport.ShowStatistics = false;
        ClickStats();
        Assert.True(viewer.CollectStatistics);
        play.Pause();
        Frame();
        Assert.False(viewer.CollectStatistics);
        Assert.Equal(0, statistics.Count);
        Assert.Null(Find("game/statistics"));
        play.Resume();
        Frame();
        Frame();
        Assert.True(statistics.Count > 0);
        play.Stop();
        Frame();
        Assert.False(viewer.CollectStatistics);
        Assert.Null(Find("game/statistics"));

        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
        {
            using (current.Node().Expand().Direction(Axis.Vertical).Enter())
            {
                using (current.Node(-1, 24, "game/header").ExpandWidth().Enter())
                    panel.RenderHeader(current, new PanelHeaderContext("game", "Game", default, false, services));
                panel.Render(current);
            }
        });

        void ClickStats()
        {
            var rect = Find("game/stats")!.Rect;
            input.MousePosition.Returns(new Vector2(rect.X + 6, rect.Y + rect.H / 2));
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
            Frame();
            Frame();
        }

        LayoutNode? Find(string id) => Nodes(gui.RootNode!).FirstOrDefault(node => node.Id == id);
        static IEnumerable<LayoutNode> Nodes(LayoutNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Nodes));
    }
}
