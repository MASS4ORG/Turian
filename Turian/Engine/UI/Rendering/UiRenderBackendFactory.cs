namespace Turian.Engine.UI;

/// <summary>Selects the UI render backend implementation. One call site for the CPU→GPU switch.</summary>
public static class UiRenderBackendFactory
{
    /// <summary>Which <see cref="IUiRenderBackend"/> implementation to build.</summary>
    public enum Mode
    {
        /// <summary>CPU SkiaSharp rasterization uploaded to a Vulkan texture each frame.</summary>
        CpuSkia,

        /// <summary>GPU Skia sharing the engine's Vulkan device. Not implemented yet.</summary>
        GpuSkia,
    }

    /// <summary>The backend used when a caller does not specify one.</summary>
    public static Mode Default { get; set; } = Mode.CpuSkia;

    /// <summary>Creates a UI render backend of the requested (or default) kind.</summary>
    /// <param name="vulkan">The shared Vulkan context.</param>
    /// <param name="width">Initial target width in pixels, greater than zero.</param>
    /// <param name="height">Initial target height in pixels, greater than zero.</param>
    /// <param name="mode">Backend kind, or <c>null</c> to use <see cref="Default"/>.</param>
    public static IUiRenderBackend Create(Vulkan vulkan, int width, int height, Mode? mode = null) =>
        (mode ?? Default) switch
        {
            Mode.GpuSkia => new GpuSkiaVulkanBackend(vulkan, width, height),
            _ => new CpuSkiaVulkanBackend(vulkan, width, height),
        };
}
