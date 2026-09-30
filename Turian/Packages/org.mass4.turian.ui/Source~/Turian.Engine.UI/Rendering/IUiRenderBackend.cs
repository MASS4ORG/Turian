namespace Turian.Engine.UI;

/// <summary>
/// Turns a Guinevere draw pass into an engine <see cref="Texture"/> that a render pass can
/// composite — full-screen for a screen-space HUD, or on a quad for a world-space panel.
///
/// <para>
/// The shipping implementation rasterizes with CPU SkiaSharp and uploads the snapshot each
/// frame (<see cref="CpuSkiaVulkanBackend"/>). The interface exists so a GPU-backed Skia
/// renderer that shares the engine's Vulkan device — no readback — can replace it without
/// touching the runtime or the render pass.
/// </para>
/// </summary>
public interface IUiRenderBackend : IDisposable
{
    /// <summary>Current target size in pixels.</summary>
    (int Width, int Height) Size { get; }

    /// <summary>
    /// The most recently rendered frame as a sampleable texture, or <c>null</c> before the
    /// first <see cref="Render"/>. The reference is stable across frames unless
    /// <see cref="Resize"/> reallocates it.
    /// </summary>
    Texture? Texture { get; }

    /// <summary>Resizes the render target. Does nothing when the size is unchanged.</summary>
    /// <param name="width">New width in pixels, greater than zero.</param>
    /// <param name="height">New height in pixels, greater than zero.</param>
    void Resize(int width, int height);

    /// <summary>
    /// Runs one UI frame: <paramref name="draw"/> receives the canvas, and the result is
    /// published to <see cref="Texture"/>.
    /// </summary>
    /// <param name="draw">Receives the canvas for one Guinevere render pass.</param>
    void Render(Action<SKCanvas> draw);
}
