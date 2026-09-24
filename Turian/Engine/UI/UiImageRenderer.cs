namespace Turian.Engine.UI;

/// <summary>
/// Renders a Guinevere UI straight to an image with no GPU involvement — for the Studio preview
/// panel, the <c>ui</c> CLI verb, tests, and golden-image comparisons. The realtime path is
/// <see cref="UiRuntime"/>.
/// </summary>
public static class UiImageRenderer
{
    /// <summary>Runs one two-pass UI frame and returns it as a PNG.</summary>
    /// <param name="build">
    /// Builds the UI; invoked once per pass, so it must be idempotent.
    /// </param>
    /// <param name="width">Image width in pixels, greater than zero.</param>
    /// <param name="height">Image height in pixels, greater than zero.</param>
    /// <param name="background">Fill color; transparent by default.</param>
    /// <param name="deltaTime">Seconds since a notional previous frame, for time-based animation.</param>
    /// <returns>PNG-encoded bytes.</returns>
    public static byte[] RenderPng(
        Action<Gui> build,
        int width,
        int height,
        SKColor? background = null,
        float deltaTime = 0f)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var canvas = surface.Canvas;
        canvas.Clear(background ?? SKColors.Transparent);

        var gui = new Gui { Input = new EngineInputHandler() };
        var font = Font.FromFamilyName("sans-serif", 16);
        gui.Time.Update(deltaTime);

        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(canvas, font, font);
        build(gui);
        gui.CalculateLayout();

        gui.SetStage(Pass.Pass2Render);
        build(gui);
        gui.Render();
        gui.EndFrame();

        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
