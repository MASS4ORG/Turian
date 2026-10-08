namespace Turian.Tests;

/// <summary>Checks that the Game viewport measures only an authored HUD in an actively playing session.</summary>
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
}
