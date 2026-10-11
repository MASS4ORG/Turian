namespace Turian.Tests;

/// <summary>Checks shared-device Skia rendering, compositing and target lifetimes on a headless Vulkan device.</summary>
[Collection(SerialTests.Name)]
public sealed class GpuSkiaVulkanBackendTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>GPU and CPU UI textures produce matching colors, orientation and transparency when composited.</summary>
    [Fact]
    public void RenderMatchesCpuAndReusesTarget()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var gpu = new GpuSkiaVulkanBackend(fixture.Vulkan, 32, 32);
        Assert.True(gpu.IsGpu, "The headless comparison must exercise the GPU backend");
        using var cpu = new CpuSkiaVulkanBackend(fixture.Vulkan, 32, 32);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 32, 32);
        Assert.Null(gpu.Texture);
        for (var frame = 0; frame < 3; frame++)
        {
            cpu.Render(DrawPattern);
            gpu.Render(DrawPattern);
            var texture = gpu.Texture!;
            Assert.Equal((32, 32), gpu.Size);
            Assert.Equal(32u, texture.Width);
            Assert.Equal(1u, texture.MipLevels);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, texture.DescriptorInfo.ImageLayout);
            var expected = Composite(viewer, cpu.Texture!);
            var actual = Composite(viewer, texture);
            for (var i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(actual[i] - expected[i]) <= 2,
                    $"Channel {i}: CPU {expected[i]}, GPU {actual[i]}");
            gpu.Render(DrawPattern);
            Assert.Same(texture, gpu.Texture);
        }
        gpu.Resize(32, 32);
        Assert.NotNull(gpu.Texture);
        Assert.Throws<InvalidOperationException>(() => gpu.Render(_ => throw new InvalidOperationException("Draw failed")));
        gpu.Render(DrawPattern);
        gpu.Resize(16, 24);
        Assert.Null(gpu.Texture);
        gpu.Render(canvas => canvas.Clear(SKColors.Blue));
        Assert.Equal((16, 24), gpu.Size);
        Assert.Equal(16u, gpu.Texture!.Width);
        Assert.Throws<InvalidOperationException>(() => gpu.Texture.Update(new byte[16 * 24 * 4]));
        gpu.Dispose();
        gpu.Dispose();
        Assert.Throws<ObjectDisposedException>(() => gpu.Render(DrawPattern));
        Assert.Throws<ObjectDisposedException>(() => gpu.Resize(4, 4));
    }

    static byte[] Composite(SceneViewerService viewer, Texture? texture)
    {
        viewer.OverlayTexture = texture;
        viewer.Render(new Node(), 0.016);
        var pixels = new byte[viewer.Width * viewer.Height * 4];
        viewer.CopyPixels(pixels);
        return pixels;
    }

    static void DrawPattern(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, 16, 16, paint);
        paint.Color = new SKColor(80, 160, 240);
        canvas.DrawRect(16, 0, 16, 16, paint);
        paint.Color = new SKColor(80, 160, 240, 128);
        canvas.DrawRect(0, 16, 16, 16, paint);
    }

    /// <summary>Premultiplied GPU colors blend like the CPU backend on a world-space panel.</summary>
    [Fact]
    public void WorldPanelMatchesCpuWithTransparency()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var gpu = new GpuSkiaVulkanBackend(fixture.Vulkan, 32, 32);
        using var cpu = new CpuSkiaVulkanBackend(fixture.Vulkan, 32, 32);
        using var viewer = new SceneViewerService(fixture.Vulkan, new AssetDatabase(), 32, 32);
        var model = Matrix4x4.CreateScale(20f) * Matrix4x4.CreateTranslation(0f, 0f, -2f);
        foreach (var color in new[] { new SKColor(80, 160, 240, 128), SKColors.Transparent, SKColors.Red })
        {
            cpu.Render(canvas => canvas.Clear(color));
            gpu.Render(canvas => canvas.Clear(color));
            viewer.WorldUiSource = _ => [new WorldUiQuad(cpu.Texture!, model)];
            var expected = Composite(viewer, null);
            viewer.WorldUiSource = _ => [new WorldUiQuad(gpu.Texture!, model)];
            var actual = Composite(viewer, null);
            for (var i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(actual[i] - expected[i]) <= 2,
                    $"World channel {i}: CPU {expected[i]}, GPU {actual[i]}");
        }
        viewer.WorldUiSource = null;
    }

    /// <summary>Invalid arguments are rejected before creating native resources.</summary>
    [Fact]
    public void ValidatesConstructionAndRenderArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new GpuSkiaVulkanBackend(null!, 1, 1));
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSkiaVulkanBackend(fixture.Vulkan, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuSkiaVulkanBackend(fixture.Vulkan, 1, -1));
        using var backend = new GpuSkiaVulkanBackend(fixture.Vulkan, 4, 4);
        Assert.Throws<ArgumentNullException>(() => backend.Render(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.Resize(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.Resize(1, 0));
        ((ICanvasRenderer)backend).Initialize(8, 8);
        Assert.Equal((8, 8), backend.Size);
    }

    /// <summary>Rejected or failing Skia contexts render through the CPU backend and preserve its resize lifecycle.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextFailureUsesCpuFallback(bool throws)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Func<GRVkBackendContext, GRContext?> create = _ => throws
            ? throw new NotSupportedException("Context unavailable") : null;
        using var backend = (GpuSkiaVulkanBackend)Activator.CreateInstance(typeof(GpuSkiaVulkanBackend),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [fixture.Vulkan, 8, 8, create], null)!;
        Assert.False(backend.IsGpu);
        backend.Render(DrawPattern);
        Assert.NotNull(backend.Texture);
        backend.Resize(8, 8);
        Assert.NotNull(backend.Texture);
        backend.Resize(16, 16);
        Assert.Null(backend.Texture);
        backend.Render(DrawPattern);
        Assert.Equal(16u, backend.Texture!.Width);
        Assert.False(backend.Texture.IsPremultipliedSrgb);
    }
}
