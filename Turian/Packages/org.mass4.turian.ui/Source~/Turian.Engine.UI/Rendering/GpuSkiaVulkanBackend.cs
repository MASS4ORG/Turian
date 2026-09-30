namespace Turian.Engine.UI;

/// <summary>
/// Placeholder for the GPU-accelerated backend: a Skia <c>GRContext</c> bound to the engine's
/// existing <c>VkDevice</c> and graphics queue, so the vector content rasterizes on the GPU and
/// the result is an image the compositor samples directly — no host readback, no per-frame copy.
///
/// <para>
/// Not implemented. The seam exists so <see cref="UiRenderBackendFactory"/> can switch to it
/// without any change at the call sites once <c>SkiaSharp.Vulkan</c> interop against Silk.NET
/// handles is in place.
/// </para>
/// </summary>
public sealed class GpuSkiaVulkanBackend : IUiRenderBackend
{
    const string notImplementedMessage =
        "GpuSkiaVulkanBackend is not implemented yet; use CpuSkiaVulkanBackend.";

    /// <summary>Creates the (not-yet-functional) GPU backend.</summary>
    /// <param name="vulkan">The shared Vulkan context the Skia GRContext would bind to.</param>
    /// <param name="width">Initial width in pixels.</param>
    /// <param name="height">Initial height in pixels.</param>
    public GpuSkiaVulkanBackend(Vulkan vulkan, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        Size = (width, height);
    }

    /// <inheritdoc />
    public (int Width, int Height) Size { get; private set; }

    /// <inheritdoc />
    public Texture? Texture => null;

    /// <inheritdoc />
    public void Resize(int width, int height) => Size = (width, height);

    /// <inheritdoc />
    public void Render(Action<SKCanvas> draw) => throw new NotImplementedException(notImplementedMessage);

    /// <inheritdoc />
    public void Dispose() { }
}
