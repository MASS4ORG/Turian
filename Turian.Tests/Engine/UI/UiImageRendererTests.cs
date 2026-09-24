namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="UiImageRenderer"/> — the no-GPU "render a Guinevere UI straight to a PNG"
/// path used by the Studio preview, the <c>ui</c> CLI verb and golden-image checks.
/// </summary>
public sealed class UiImageRendererTests
{
    /// <summary>Rendering a simple UI produces a decodable PNG of the requested size.</summary>
    [Fact]
    public void RenderPng_ProducesDecodablePngOfRequestedSize()
    {
        var png = UiImageRenderer.RenderPng(
            gui =>
            {
                using (gui.Node(240, 80).Enter())
                {
                    gui.DrawBackgroundRect(GuiColor.Blue);
                    gui.DrawText("Guinevere in Turian");
                }
            },
            width: 320,
            height: 160,
            background: new SKColor(16, 16, 20));

        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(160, bitmap.Height);

        // Save an artifact next to the test binary so the result can be eyeballed.
        var outPath = Path.Combine(AppContext.BaseDirectory, "ui-preview.png");
        File.WriteAllBytes(outPath, png);
    }

    /// <summary>The drawn content actually lands on the canvas (not a blank fill).</summary>
    [Fact]
    public void RenderPng_ContainsDrawnColor()
    {
        var png = UiImageRenderer.RenderPng(
            gui =>
            {
                using (gui.Node(300, 140).Enter())
                    gui.DrawBackgroundRect(GuiColor.Red);
            },
            width: 320,
            height: 160);

        using var bitmap = SKBitmap.Decode(png);
        var hitRed = false;
        for (var y = 0; y < bitmap.Height && !hitRed; y += 8)
            for (var x = 0; x < bitmap.Width && !hitRed; x += 8)
            {
                var c = bitmap.GetPixel(x, y);
                if (c is { Red: > 180, Green: < 80, Blue: < 80 }) hitRed = true;
            }

        Assert.True(hitRed, "expected the red panel to appear in the rendered image");
    }

    /// <summary>Zero or negative dimensions are rejected before any drawing happens.</summary>
    [Fact]
    public void RenderPng_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiImageRenderer.RenderPng(_ => { }, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiImageRenderer.RenderPng(_ => { }, 100, -5));
    }
}
