namespace Turian.Tests;

/// <summary>
/// Integration coverage for <see cref="CpuSkiaVulkanBackend"/> and <see cref="Texture.Update"/>:
/// a real headless Vulkan device, a Skia frame, the initial texture upload and the per-frame
/// re-upload. Skipped when no usable Vulkan device is present.
/// </summary>
public sealed class CpuSkiaVulkanBackendTests : IClassFixture<VulkanFixture>
{
    readonly VulkanFixture fixture;

    /// <summary>Receives the shared headless Vulkan device fixture.</summary>
    public CpuSkiaVulkanBackendTests(VulkanFixture fixture) => this.fixture = fixture;

    static void FillRed(SKCanvas canvas)
    {
        using (var paint = new SKPaint())
        {
            paint.Color = SKColors.Red;
            canvas.DrawRect(new SKRect(0, 0, 4096, 4096), paint);
        }
    }

    /// <summary>The first render creates a sampleable texture at the target size.</summary>
    [Fact]
    public void Render_FirstFrame_CreatesTextureAtTargetSize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var backend = new CpuSkiaVulkanBackend(fixture.Vulkan, 4, 4);
        Assert.Null(backend.Texture);

        backend.Render(FillRed);

        Assert.NotNull(backend.Texture);
        Assert.Equal((4, 4), backend.Size);
        Assert.Equal(4u, backend.Texture!.Width);
        Assert.Equal(4u, backend.Texture.Height);
        Assert.Equal(1u, backend.Texture.MipLevels);
    }

    /// <summary>Subsequent renders re-upload into the same texture instance.</summary>
    [Fact]
    public void Render_SecondFrame_ReusesTheSameTexture()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var backend = new CpuSkiaVulkanBackend(fixture.Vulkan, 8, 8);
        backend.Render(FillRed);
        var first = backend.Texture;

        backend.Render(FillRed);

        Assert.Same(first, backend.Texture);
    }

    /// <summary>Resizing drops the texture; the next render rebuilds it at the new size.</summary>
    [Fact]
    public void Resize_ThenRender_RebuildsTextureAtNewSize()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var backend = new CpuSkiaVulkanBackend(fixture.Vulkan, 4, 4);
        backend.Render(FillRed);

        backend.Resize(16, 12);
        Assert.Null(backend.Texture);

        backend.Render(FillRed);

        Assert.Equal((16, 12), backend.Size);
        Assert.Equal(16u, backend.Texture!.Width);
        Assert.Equal(12u, backend.Texture.Height);
    }
}
