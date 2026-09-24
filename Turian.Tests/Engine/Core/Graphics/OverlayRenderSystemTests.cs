namespace Turian.Tests;

/// <summary>
/// Integration coverage for <see cref="OverlayRenderSystem"/> through <see cref="SceneViewerService"/>:
/// a texture set as the overlay is composited over the rendered frame and shows up in the
/// offscreen read-back. Skipped when no usable Vulkan device is present.
/// </summary>
public sealed class OverlayRenderSystemTests : IClassFixture<VulkanFixture>
{
    const int size = 16;
    readonly VulkanFixture fixture;

    /// <summary>Receives the shared headless Vulkan device fixture.</summary>
    public OverlayRenderSystemTests(VulkanFixture fixture) => this.fixture = fixture;

    static Texture OpaqueRed(Vulkan vulkan)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = 255; // R
            rgba[i + 3] = 255; // A
        }

        return new Texture(vulkan, size, size, rgba, isSrgb: false, generateMips: false,
            SamplerAddressMode.ClampToEdge);
    }

    /// <summary>With no overlay texture the pass draws nothing and the frame is unchanged.</summary>
    [Fact]
    public void NoOverlay_LeavesFrameUnchanged()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var svc = new SceneViewerService(fixture.Vulkan, size, size);
        var root = new Node();

        svc.Render(root, 0.016);
        var baseline = new byte[size * size * 4];
        svc.CopyPixels(baseline);

        svc.Render(root, 0.016);
        var again = new byte[size * size * 4];
        svc.CopyPixels(again);

        Assert.Equal(baseline, again);
    }

    /// <summary>An opaque red overlay makes every pixel read back red (BGRA8 target).</summary>
    [Fact]
    public void OpaqueOverlay_PaintsWholeFrame()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var svc = new SceneViewerService(fixture.Vulkan, size, size);
        using var red = OpaqueRed(fixture.Vulkan);
        var root = new Node();

        svc.OverlayTexture = red;
        svc.Render(root, 0.016);

        var pixels = new byte[size * size * 4];
        svc.CopyPixels(pixels);

        // OffscreenFrameTarget.ColorFormat is B8G8R8A8Unorm, so red is (B=0, G=0, R=255, A=255).
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i + 0] <= 2, $"B at {i} was {pixels[i + 0]}");
            Assert.True(pixels[i + 1] <= 2, $"G at {i} was {pixels[i + 1]}");
            Assert.True(pixels[i + 2] >= 253, $"R at {i} was {pixels[i + 2]}");
            Assert.True(pixels[i + 3] >= 253, $"A at {i} was {pixels[i + 3]}");
        }
    }

    /// <summary>Clearing the overlay stops compositing; the frame returns to the no-overlay result.</summary>
    [Fact]
    public void ClearingOverlay_StopsCompositing()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var svc = new SceneViewerService(fixture.Vulkan, size, size);
        using var red = OpaqueRed(fixture.Vulkan);
        var root = new Node();

        svc.Render(root, 0.016);
        var baseline = new byte[size * size * 4];
        svc.CopyPixels(baseline);

        svc.OverlayTexture = red;
        svc.Render(root, 0.016);

        svc.OverlayTexture = null;
        svc.Render(root, 0.016);
        var cleared = new byte[size * size * 4];
        svc.CopyPixels(cleared);

        Assert.Equal(baseline, cleared);
    }
}
