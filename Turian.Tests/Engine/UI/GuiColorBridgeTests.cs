using EngineColor = global::Turian.Engine.Core.Color;

namespace Turian.Tests;

/// <summary>Checks engine-color drawing bridges preserve sRGB colors and background alpha in direct commands.</summary>
public sealed class GuiColorBridgeTests
{
    /// <summary>Void drawing delegates preserve converted fills, borders and translucent backgrounds.</summary>
    [Fact]
    public void DirectCommandsPreserveEngineColorsAndAlpha()
    {
        var fill = EngineColor.FromSrgb(new Color32(128, 64, 32, 255));
        var border = EngineColor.FromSrgb(new Color32(0, 0, 255, 255));
        var background = EngineColor.FromSrgb(new Color32(10, 20, 30, 128));
        Action<Gui, Guinevere.Rect, EngineColor, float, Guinevere.Corner> drawRect = GuiColorBridge.DrawRect;
        Action<Gui, Guinevere.Rect, EngineColor, float, float, Guinevere.Corner> drawBorder =
            GuiColorBridge.DrawRectBorder;
        Action<Gui, EngineColor?, float, Guinevere.Corner> drawBackground = GuiColorBridge.DrawBackgroundRect;
        var png = UiImageRenderer.RenderPng(gui =>
        {
            drawRect(gui, new Guinevere.Rect(10, 10, 20, 20), fill, 0f, Guinevere.Corner.All);
            drawBorder(gui, new Guinevere.Rect(40, 10, 20, 20), border, 2f, 0f, Guinevere.Corner.All);
            drawBackground(gui, background, 0f, Guinevere.Corner.All);
        }, 80, 80, background: SKColors.Transparent);
        using var pixels = SKBitmap.Decode(png);
        Assert.Equal(new SKColor(128, 64, 32), pixels.GetPixel(20, 20));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(40, 20));
        var backdrop = pixels.GetPixel(50, 20);
        Assert.Equal(128, backdrop.Alpha);
        Assert.InRange(backdrop.Red, 9, 11);
        Assert.InRange(backdrop.Green, 19, 21);
        Assert.InRange(backdrop.Blue, 29, 31);
    }

    /// <summary>A missing engine background color uses the direct API's default white.</summary>
    [Fact]
    public void NullBackgroundUsesWhite()
    {
        Action<Gui, EngineColor?, float, Guinevere.Corner> drawBackground = GuiColorBridge.DrawBackgroundRect;
        var png = UiImageRenderer.RenderPng(gui => drawBackground(gui, null, 0f, Guinevere.Corner.All),
            40, 40, background: SKColors.Transparent);
        using var pixels = SKBitmap.Decode(png);
        Assert.Equal(SKColors.White, pixels.GetPixel(20, 20));
    }
}
