namespace Turian.Tests;

/// <summary>Checks that the Game view clears with the scene camera's background, as the built game does.</summary>
[Collection(SerialTests.Name)]
public sealed class GameViewportBackgroundTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>
    /// The game preview clears with the camera's light blue default and follows live edits of it; the Scene view's
    /// sky preference does not reach it.
    /// </summary>
    [Fact]
    public void GamePreviewUsesTheCameraBackground()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, Substitute.For<IAssetLoader>(),
            Substitute.For<ISceneManager>(), database);
        var root = new Node();
        var camera = new CameraComponent();
        root.AddComponent(camera);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tree, root);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        var preferences = new EditorCameraSettings();
        using var viewport = new GameViewport(fixture.Vulkan, database, tree, play, NullLogger.Instance, preferences);
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(320, 200));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
        var viewer = (SceneViewerService)typeof(GameViewport).GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewport)!;
        Assert.Equal((Vector4)camera.BackgroundColor, viewer.ClearColor);
        Assert.False(viewer.CollectStatistics);
        Assert.DoesNotContain(Descendants(gui.RootNode!), node => node.Id.Contains("statistics"));
        Assert.Equal("", (string?)typeof(GameViewport).GetField("failure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewport) ?? "");
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        Assert.True(pixels[0] > pixels[1]);
        Assert.True(pixels[1] > pixels[2]);
        Assert.InRange(pixels[0], (byte)240, (byte)255);
        preferences.View.EmptySkyColor = new Vector3(0.9f, 0f, 0f);
        camera.BackgroundColor = new Color(0.1f, 0.2f, 0.3f, 1f);
        InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), viewer.ClearColor);
        viewer.CopyPixels(pixels);
        Assert.True(pixels[0] > pixels[1]);
        Assert.True(pixels[1] > pixels[2]);
        var imageField = typeof(GameViewport).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousFrame = imageField.GetValue(viewport);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, new Node());
        InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
        Assert.Same(previousFrame, imageField.GetValue(viewport));
    }

    /// <summary>An unavailable rendering device leaves the rest of the Studio frame usable.</summary>
    [Fact]
    public void MissingDeviceKeepsTheWorkbenchRenderable()
    {
        var database = new AssetDatabase();
        var tree = new SceneTreeController(new AssetManager(), new SettingsService(), null!,
            Substitute.For<IAssetLoader>(), Substitute.For<ISceneManager>(), database);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        using var viewport = new GameViewport(null!, database, tree, play, NullLogger.Instance);
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(320, 200));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
        Assert.Equal("Game rendering is unavailable — see the log.",
            typeof(GameViewport).GetField("failure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport));
        InspectorFormsRenderingTests.Frame(gui, surface, font, viewport.Render);
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
