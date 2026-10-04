namespace Turian.Tests;

/// <summary>Checks scene toolbar culling statistics through both headless GUI passes.</summary>
[Collection(SerialTests.Name)]
public sealed class ScenePanelCullingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>A populated viewport shows a stats node, and an empty view removes it.</summary>
    [Fact]
    public void ToolbarShowsCountsOnlyForDrawableContent()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, null!,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        var viewport = new SceneViewport(fixture.Vulkan, database, tree, inspector, null!, null!,
            new EditorCameraSettings(), NullLogger.Instance, null!, new LocaleService());
        var viewer = new SceneViewerService(fixture.Vulkan, database, 32, 32);
        typeof(SceneViewport).GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewport, viewer);
        using var panel = new ScenePanel(viewport, tree, inspector, null!, null!);
        var toolbar = typeof(ScenePanel).GetMethod("Toolbar", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1f));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(960, 100));
        var font = Font.FromFamilyName("sans-serif", 14);
        var root = new Node();
        var child = new Node { Position = new Vector3(0f, 0f, 4f), Parent = root };
        child.AddComponent(new ModelComponent { ModelOverride = PreviewQuadMesh.Get(fixture.Vulkan) });
        root.Children.Add(child);
        viewer.Render(root, 0.016);

        InspectorFormsRenderingTests.Frame(gui, surface, font, current => toolbar.Invoke(panel, [current]));
        var stats = Assert.Single(Descendants(gui.RootNode!), node => node.Id == "scene/toolbar/culling");
        var snap = Assert.Single(Descendants(gui.RootNode!), node => node.Id == "scene/toolbar/snapLabel");
        Assert.True(stats.Rect.X >= snap.Rect.X + snap.Rect.W);
        Assert.Equal(new RenderCullingStats(1, 0), viewport.CullingStats);

        root.Children.Clear();
        viewer.Render(root, 0.016);
        InspectorFormsRenderingTests.Frame(gui, surface, font, current => toolbar.Invoke(panel, [current]));
        Assert.DoesNotContain(Descendants(gui.RootNode!), node => node.Id == "scene/toolbar/culling");
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
