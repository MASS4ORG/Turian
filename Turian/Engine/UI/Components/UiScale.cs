namespace Turian.Engine.UI;

/// <summary>How a screen-space panel's logical size relates to the framebuffer size.</summary>
public enum UiScaleMode
{
    /// <summary>One UI unit is one physical pixel. The panel is exactly the framebuffer size.</summary>
    ConstantPixelSize = 0,

    /// <summary>
    /// The panel keeps a reference resolution and scales with the framebuffer, so layouts made for
    /// one screen size adapt to others. The scale is the log-space average of the width and height
    /// ratios (Unity's default "match 0.5").
    /// </summary>
    ScaleWithScreenSize = 1,

    /// <summary>
    /// A fixed physical size regardless of resolution. Needs display DPI, which the engine does not
    /// surface yet, so this currently behaves like <see cref="ConstantPixelSize"/>.
    /// </summary>
    ConstantPhysicalSize = 2,
}

/// <summary>
/// Resolves a framebuffer size and a <see cref="UiScaleMode"/> into the pixel size a Guinevere
/// frame is rasterized at and the canvas scale applied before building it.
/// </summary>
public readonly record struct UiScale(int PixelWidth, int PixelHeight, float CanvasScale)
{
    /// <summary>
    /// Computes the scale for a panel.
    /// </summary>
    /// <param name="framebufferWidth">Target width in physical pixels, greater than zero.</param>
    /// <param name="framebufferHeight">Target height in physical pixels, greater than zero.</param>
    /// <param name="mode">The scaling mode.</param>
    /// <param name="referenceWidth">Reference width for <see cref="UiScaleMode.ScaleWithScreenSize"/>.</param>
    /// <param name="referenceHeight">Reference height for <see cref="UiScaleMode.ScaleWithScreenSize"/>.</param>
    public static UiScale Resolve(
        int framebufferWidth,
        int framebufferHeight,
        UiScaleMode mode,
        int referenceWidth = 1920,
        int referenceHeight = 1080)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framebufferWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framebufferHeight);

        if (mode != UiScaleMode.ScaleWithScreenSize || referenceWidth <= 0 || referenceHeight <= 0)
            return new UiScale(framebufferWidth, framebufferHeight, 1f);

        var logWidth = MathF.Log2((float)framebufferWidth / referenceWidth);
        var logHeight = MathF.Log2((float)framebufferHeight / referenceHeight);
        var scale = MathF.Pow(2f, (logWidth + logHeight) * 0.5f);

        return new UiScale(framebufferWidth, framebufferHeight, scale);
    }
}
