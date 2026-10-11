namespace Turian.Tests;

/// <summary>Checks scene-authored HUD lifecycle, settings, frame data and editor exclusion.</summary>
[Collection(SerialTests.Name)]
public sealed class FrameStatisticsHudComponentTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>The HUD is discoverable only after starting and can be hidden or disabled by its owner.</summary>
    [Fact]
    public void AuthoredComponentStartsAndResetsWithoutAllocating()
    {
        var root = new Node();
        var child = new Node { Parent = root };
        root.Children.Add(child);
        var component = new FrameStatisticsHudComponent();
        Assert.Null(RenderStatisticsTargets.Find(root));
        RenderStatisticsTargets.Record(null, default);
        child.AddComponent(component);
        component.EnsureStarted();
        Assert.True(component.PlayModeOnly);
        Assert.NotNull(component.OnBuild);
        Assert.Same(component, RenderStatisticsTargets.Find(root));
        RenderStatisticsTargets.Record(component, new RenderFrameStats { DrawCalls = 4 });
        RenderStatisticsTargets.Record(component, new RenderFrameStats { DrawCalls = 4 });
        Assert.True(component.Statistics.Count > 0);
        for (var i = 0; i < 100; i++) RenderStatisticsTargets.Find(root);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) RenderStatisticsTargets.Find(root);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        component.Statistics.Add(0.05, new RenderFrameStats { DrawCalls = 4 });
        component.ShowHud = false;
        RenderStatisticsTargets.Record(component, default);
        Assert.Equal(0, component.Statistics.Count);
        Assert.Null(RenderStatisticsTargets.Find(root));
        component.ShowHud = true;
        component.IsActive = false;
        Assert.Null(RenderStatisticsTargets.Find(root));
        component.IsActive = true;
        child.IsActive = false;
        Assert.Null(RenderStatisticsTargets.Find(root));
        child.IsActive = true;
        Assert.Same(component, RenderStatisticsTargets.Find(root));
        component.Detach();
        Assert.Null(component.OnBuild);
    }

    /// <summary>The same started HUD is excluded from preview but draws through the game UI while playing.</summary>
    [Fact]
    public void PresenterRequiresPlayAndDrawsActualStatistics()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var root = new Node();
        var component = new FrameStatisticsHudComponent { SceneLoadMilliseconds = 123.4 };
        root.AddComponent(component);
        using var manager = new UiManager(fixture.Vulkan);
        Assert.Null(manager.RenderOverlay(root, 450, 180, 0));
        var copy = NodeCloner.DeepClone(root);
        Assert.NotNull(copy);
        manager.IsPlaying = true;
        Assert.Null(manager.RenderOverlay(copy, 450, 180, 0));
        manager.IsPlaying = false;
        component.EnsureStarted();
        component.Statistics.Add(0.05, new RenderFrameStats { DrawCalls = 4, Triangles = 12 });
        Assert.Null(manager.RenderOverlay(root, 450, 180, 0));
        Assert.Empty(manager.RenderWorldPanels(root, new WorldUiFrame(new EditorCamera(), 450, 180, 0)));
        manager.IsPlaying = true;
        Assert.NotNull(manager.RenderOverlay(root, 450, 180, 0));
        component.ShowHud = false;
        Assert.Null(manager.RenderOverlay(root, 450, 180, 0));
        component.ShowHud = true;
        Assert.NotNull(manager.RenderOverlay(root, 450, 180, 0));
        component.Statistics.Add(0.05, new RenderFrameStats { DrawCalls = 4 });
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        using var surface = SKSurface.Create(new SKImageInfo(450, 180));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(gui, surface, font, component.OnBuild!);
        var hud = gui.RootNode!.Children.Single(node => node.Id == "frame-statistics");
        Assert.False(hud.Style.BlocksInput);
        Assert.Equal(component.HudPosition.X, hud.Rect.X);
        Assert.Equal(component.HudSize.X, hud.Rect.W);
        var drawing = (IDrawable)typeof(FrameStatisticsHudComponent)
            .GetField("drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        Assert.NotNull(drawing.Paint);
        for (var i = 0; i < 100; i++) drawing.Render(gui, hud, surface.Canvas);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) drawing.Render(gui, hud, surface.Canvas);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        component.FontSize = 14;
        component.SceneLoadMilliseconds = null;
        drawing.Render(gui, hud, surface.Canvas);
        root.IsActive = false;
        Assert.Null(manager.RenderOverlay(root, 450, 180, 0));
        component.Detach();
    }

    /// <summary>Inspector settings round-trip without persisting collected samples or the runtime builder.</summary>
    [Fact]
    public void SettingsRoundTripAsAComponent()
    {
        TypeRegistry.ScanAssembly(typeof(FrameStatisticsHudComponent).Assembly);
        var root = new Node();
        var component = new FrameStatisticsHudComponent
        {
            ShowHud = false,
            HudPosition = new Vector2(20, 30),
            HudSize = new Vector2(420, 140),
            FontSize = 13,
        };
        root.AddComponent(component);
        var json = Serializer.Serialize(root);
        var copy = Serializer.LoadData<Node>(json)!.GetComponent<FrameStatisticsHudComponent>()!;
        Assert.False(copy.ShowHud);
        Assert.Equal(component.HudPosition, copy.HudPosition);
        Assert.Equal(component.HudSize, copy.HudSize);
        Assert.Equal(13, copy.FontSize);
        Assert.Null(copy.OnBuild);
        Assert.Equal(0, copy.Statistics.Count);
        Assert.DoesNotContain("SceneLoadMilliseconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Statistics", json, StringComparison.Ordinal);
    }
}
