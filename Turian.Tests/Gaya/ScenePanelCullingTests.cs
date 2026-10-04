namespace Turian.Tests;

/// <summary>Checks that rendering diagnostics are available without occupying the Scene toolbar.</summary>
[Collection(SerialTests.Name)]
public sealed class ScenePanelCullingTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Culling counts remain available to diagnostics while the toolbar contains only tools.</summary>
    [Fact]
    public void ToolbarOmitsCullingCounts()
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
        using var owner = viewport;
        var toolbar = new SceneToolbar(viewport, () => { });
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

        InspectorFormsRenderingTests.Frame(gui, surface, font, toolbar.Render);
        Assert.DoesNotContain(Descendants(gui.RootNode!), node => node.Id == "scene/toolbar/culling");
        Assert.Equal(new RenderCullingStats(1, 0), viewport.CullingStats);

        root.Children.Clear();
        viewer.Render(root, 0.016);
        InspectorFormsRenderingTests.Frame(gui, surface, font, toolbar.Render);
        Assert.DoesNotContain(Descendants(gui.RootNode!), node => node.Id == "scene/toolbar/culling");
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
