namespace Turian.Engine.UI;

/// <summary>
/// UI render backend that rasterizes the frame with CPU SkiaSharp and uploads the snapshot
/// into an engine <see cref="Texture"/> once per frame. Also usable anywhere Guinevere expects
/// an <see cref="ICanvasRenderer"/>.
/// </summary>
/// <remarks>
/// Per-frame cost is one CPU rasterization plus a staged host→device copy that blocks the
/// graphics queue (see <see cref="Texture.Update"/>). Adequate for HUDs and modest panels; the
/// GPU-shared-device path (<see cref="GpuSkiaVulkanBackend"/>) is the answer for large,
/// continuously-animating surfaces.
/// </remarks>
public sealed class CpuSkiaVulkanBackend : IUiRenderBackend, ICanvasRenderer
{
    readonly Vulkan vulkan;
    readonly SkiaFrameRasterizer rasterizer;
    Texture? texture;
    bool disposed;

    /// <summary>Creates the backend on a Vulkan context with an initial target size.</summary>
    /// <param name="vulkan">The shared Vulkan context textures are created on.</param>
    /// <param name="width">Initial width in pixels, greater than zero.</param>
    /// <param name="height">Initial height in pixels, greater than zero.</param>
    public CpuSkiaVulkanBackend(Vulkan vulkan, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;
        rasterizer = new SkiaFrameRasterizer(width, height);
    }

    /// <inheritdoc />
    public (int Width, int Height) Size => (rasterizer.Width, rasterizer.Height);

    /// <inheritdoc />
    public Texture? Texture => texture;

    /// <summary>No-op: the target size is supplied at construction and through <see cref="Resize"/>.</summary>
    void ICanvasRenderer.Initialize(int width, int height) => Resize(width, height);

    /// <summary>Resizes the raster target and discards the previous texture.</summary>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((width, height) == Size && texture is not null) return;

        rasterizer.Resize(width, height);
        texture?.Dispose();
        texture = null;
    }

    /// <summary>Rasterizes one frame and uploads it to the Vulkan texture.</summary>
    public void Render(Action<SKCanvas> draw)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var pixels = rasterizer.Render(draw);

        if (texture is null)
        {
            texture = new Texture(
                vulkan,
                (uint)rasterizer.Width,
                (uint)rasterizer.Height,
                pixels,
                isSrgb: false,
                generateMips: false,
                SamplerAddressMode.ClampToEdge);
        }
        else
        {
            texture.Update(pixels);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        texture?.Dispose();
        texture = null;
        rasterizer.Dispose();
    }
}
