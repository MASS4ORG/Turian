namespace Turian.Tests;

/// <summary>Checks component gizmos in the running Scene view without enabling transform editing.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneViewportPlayGizmoTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Running and paused scenes render selected component gizmos while transform handles stay hidden.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayShowsComponentGizmosWithoutTransformHandles(bool paused)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        var root = new Node();
        var light = new Node { Name = "light", Position = new Vector3(0, 1, 0), Parent = root };
        light.AddComponent(new LightComponent());
        root.Children.Add(light);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(tree, database, services, NullLogger.Instance);
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var settings = new EditorCameraSettings();
        settings.Grid.Visible = false;
        settings.Gizmos.ShowOrientation = false;
        settings.Gizmos.Visible = false;
        using var viewport = new SceneViewport(fixture.Vulkan, database, tree, inspector,
            new GizmoDrawerCatalog(build), play, settings, NullLogger.Instance, undo, new LocaleService());
        Assert.True(play.Start());
        try
        {
            if (paused)
            {
                play.Pause();
            }

            var runningLight = Assert.Single(play.PlayRoot!.Children);
            Assert.NotSame(light, runningLight);
            Assert.Same(play.PlayRoot, tree.CurrentSceneRoot);
            inspector.Select(runningLight);
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var gui = new Gui { Input = input };
            using var surface = SKSurface.Create(new SKImageInfo(800, 450));
            var font = Font.FromFamilyName("sans-serif", 14);
            Frame();
            Assert.Null(typeof(SceneViewport).GetField("failure", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewport));
            var viewer = (SceneViewerService)typeof(SceneViewport)
                .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            var hidden = new byte[800 * 450 * 4];
            viewer.CopyPixels(hidden);
            Assert.Empty(viewer.Gizmos.WorldLines);
            settings.Gizmos.Visible = true;
            Frame();

            Assert.NotEmpty(viewer.Gizmos.WorldLines);
            Assert.Empty(viewer.Gizmos.OverlayTriangles);
            var visible = new byte[hidden.Length];
            viewer.CopyPixels(visible);
            Assert.False(hidden.AsSpan().SequenceEqual(visible), "component wires must reach the rendered image");
            inspector.SelectNode(null);
            Frame();
            Assert.Empty(viewer.Gizmos.WorldLines);
            settings.Gizmos.Visible = false;
            inspector.Select(runningLight);
            Frame();
            Assert.Empty(viewer.Gizmos.WorldLines);
            Assert.Equal(Transform.Identity, root.Transform);

            void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
            {
                using (current.Node(800, 450, "viewport").Enter())
                {
                    viewport.Render(current);
                }
            });
        }
        finally
        {
            play.Stop();
        }
    }
}
