namespace Turian.Tests;

/// <summary>
/// Integration coverage for <see cref="UiRuntime"/> and <see cref="UiManager"/> on a real headless
/// Vulkan device: a Guinevere frame becomes a texture, and the manager discovers screen-space
/// panels in a scene and composites them in sort order. Skipped when no usable Vulkan device.
/// </summary>
public sealed class UiRuntimeTests : IClassFixture<VulkanFixture>
{
    readonly VulkanFixture fixture;

    /// <summary>Receives the shared headless Vulkan device fixture.</summary>
    public UiRuntimeTests(VulkanFixture fixture) => this.fixture = fixture;

    /// <summary>One frame produces a texture at the requested size.</summary>
    [Fact]
    public void Render_ProducesTextureAtSize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var runtime = new UiRuntime(fixture.Vulkan, 128, 64);

        var tex = runtime.Render(
            gui =>
            {
                using (gui.Node(80, 40).Enter())
                    gui.DrawBackgroundRect(GuiColor.Red);
            },
            128, 64, deltaTime: 0.016f);

        Assert.Equal(128u, tex.Width);
        Assert.Equal(64u, tex.Height);
        Assert.Same(tex, runtime.Texture);
    }

    /// <summary>A canvas scale other than 1 renders without error and keeps the surface size.</summary>
    [Fact]
    public void Render_WithCanvasScale_DoesNotChangeSurfaceSize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var runtime = new UiRuntime(fixture.Vulkan, 200, 100);

        var tex = runtime.Render(_ => { }, 200, 100, deltaTime: 0f, canvasScale: 1.5f);

        Assert.Equal((200, 100), runtime.Size);
        Assert.Equal(200u, tex.Width);
    }

    /// <summary>The manager builds enabled screen-space panels found in the scene in sort order.</summary>
    [Fact]
    public void RenderOverlay_InvokesPanelsInSortOrder()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var manager = new UiManager(fixture.Vulkan);
        var order = new List<int>();

        var root = new Node { Name = "Root" };
        foreach (var sort in new[] { 10, -5, 3 })
        {
            var child = new Node { Name = $"panel{sort}" };
            var captured = sort;
            child.AddComponent(new UiDocumentComponent
            {
                Mode = UiRenderMode.ScreenSpaceOverlay,
                SortOrder = sort,
                OnBuild = _ => order.Add(captured),
            });
            child.Parent = root;
            root.Children.Add(child);
        }

        root.Awake(null);

        var tex = manager.RenderOverlay(root, 256, 128, 0.016f);

        Assert.NotNull(tex);
        // The build callback runs once per Guinevere pass (layout, then render), so each panel is
        // visited twice — the ordering within a pass is what matters.
        Assert.Equal(new[] { -5, 3, 10 }, order.Take(3));
        Assert.Equal(new[] { -5, 3, 10 }, order.Skip(3).Take(3));
    }

    /// <summary>A scene with no screen-space panels composites nothing.</summary>
    [Fact]
    public void RenderOverlay_NoPanels_ReturnsNull()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var manager = new UiManager(fixture.Vulkan);
        var root = new Node { Name = "Empty" };
        root.Awake(null);

        Assert.Null(manager.RenderOverlay(root, 256, 128, 0.016f));
    }

    /// <summary>An inactive component is skipped by the compositor.</summary>
    [Fact]
    public void RenderOverlay_SkipsInactivePanels()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var manager = new UiManager(fixture.Vulkan);
        var built = 0;

        var root = new Node { Name = "Root" };
        var child = new Node { Name = "panel" };
        var component = new UiDocumentComponent
        {
            Mode = UiRenderMode.ScreenSpaceOverlay,
            OnBuild = _ => built++,
        };
        child.AddComponent(component);
        child.Parent = root;
        root.Children.Add(child);
        root.Awake(null);

        component.IsActive = false;

        Assert.Null(manager.RenderOverlay(root, 128, 64, 0.016f));
        Assert.Equal(0, built);
    }
}
