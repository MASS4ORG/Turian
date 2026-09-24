namespace Turian.Tests;

/// <summary>
/// Integration coverage for <see cref="WorldUiRenderSystem"/> through <see cref="SceneViewerService"/>:
/// a <see cref="WorldUiQuad"/> supplied via <see cref="SceneViewerService.WorldUiSource"/> is drawn
/// into the scene and shows up in the offscreen read-back. Skipped without a Vulkan device.
/// </summary>
public sealed class WorldUiRenderSystemTests : IClassFixture<VulkanFixture>
{
    const int size = 32;
    readonly VulkanFixture fixture;

    /// <summary>Instantiates the fixture shared by the Vulkan-backed tests.</summary>
    public WorldUiRenderSystemTests(VulkanFixture fixture) => this.fixture = fixture;

    static Texture OpaqueGreen(Vulkan vulkan)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 1] = 255; // G
            rgba[i + 3] = 255; // A
        }

        return new Texture(vulkan, size, size, rgba, isSrgb: false, generateMips: false,
            SamplerAddressMode.ClampToEdge);
    }

    /// <summary>With no panels supplied the frame matches the empty baseline.</summary>
    [Fact]
    public void NoPanels_LeavesFrameUnchanged()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var svc = new SceneViewerService(fixture.Vulkan, size, size);
        var root = new Node();

        svc.Render(root, 0.016);
        var baseline = new byte[size * size * 4];
        svc.CopyPixels(baseline);

        svc.WorldUiSource = _ => Array.Empty<WorldUiQuad>();
        svc.Render(root, 0.016);
        var again = new byte[size * size * 4];
        svc.CopyPixels(again);

        Assert.Equal(baseline, again);
    }

    /// <summary>A green quad in front of the camera is visible in the offscreen read-back.</summary>
    [Fact]
    public void Panel_InFrontOfCamera_IsDrawn()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        // Keep the texture alive while the source closure can still be called.
        using (var green = OpaqueGreen(fixture.Vulkan))
        using (var svc = new SceneViewerService(fixture.Vulkan, size, size))
        {
            var root = new Node();

            // Default EditorCamera sits at the origin looking down -Z; put a big quad two metres ahead.
            var model = Matrix4x4.CreateScale(20f) * Matrix4x4.CreateTranslation(0f, 0f, -2f);
            var quads = new[] { new WorldUiQuad(green, model) };
            svc.WorldUiSource = _ => quads;
            svc.Render(root, 0.016);

            var pixels = new byte[size * size * 4];
            svc.CopyPixels(pixels);

            // OffscreenFrameTarget is B8G8R8A8Unorm; the centre pixel should be the panel's green.
            var centre = (((size / 2) * size) + (size / 2)) * 4;
            Assert.True(pixels[centre + 1] > 200 && pixels[centre + 0] < 80 && pixels[centre + 2] < 80,
                $"expected green panel at centre, got B={pixels[centre]} G={pixels[centre + 1]} R={pixels[centre + 2]}");
            svc.WorldUiSource = null;
        }
    }
}
