namespace Turian.Engine.UI;

/// <summary>
/// Converts between the engine's linear-light <see cref="Turian.Engine.Core.Color"/> and <see cref="GuiColor"/>
/// (Guinevere's packed sRGB color), and lets the editor pass engine colors straight to the
/// Guinevere draw calls it actually uses. Neither assembly can reference the other's color type
/// directly — the engine can't depend on Guinevere, and Guinevere can't depend on the engine —
/// so this bridge lives in <c>Turian.Engine.UI</c>, which already references both. A user-defined
/// implicit conversion isn't an option either way: C# rejects conversion operators declared
/// inside extension blocks (CS9282).
/// </summary>
public static class GuiColorBridge
{
    /// <summary>Encodes a linear engine color to Guinevere's packed sRGB color, clamping to 0..255.</summary>
    public static GuiColor ToGui(this Core.Color color)
    {
        var srgb = color.ToSrgb();
        return GuiColor.FromArgb(srgb.A, srgb.R, srgb.G, srgb.B);
    }

    /// <summary>Decodes a Guinevere sRGB color to the engine's linear-light color.</summary>
    public static Core.Color ToEngine(this GuiColor color) =>
        Core.Color.FromSrgb(new Color32(color.R, color.G, color.B, color.A));

    /// <inheritdoc cref="Gui.DrawText"/>
    public static LayoutNode DrawText(
        this Gui gui,
        string text,
        float size = 0,
        Core.Color? color = null,
        Font? font = null,
        float wrapWidth = 0,
        bool centerInRect = true,
        bool clip = false,
        TextEffects? effects = null) =>
        gui.DrawText(text, size, color?.ToGui(), font, wrapWidth, centerInRect, clip, effects);

    /// <inheritdoc cref="Gui.DrawRect(Rect, GuiColor, float, Corner)"/>
    public static Shape DrawRect(this Gui gui, Rect rect, Core.Color color,
        float radius = 0.0f, Corner corners = Corner.All) =>
        gui.DrawRect(rect, color.ToGui(), radius, corners);

    /// <inheritdoc cref="Gui.DrawRectBorder(Rect, GuiColor, float, float, Corner)"/>
    public static Shape DrawRectBorder(this Gui gui, Rect screenRect, Core.Color color, float thickness = 1f,
        float radius = 0.0f, Corner corners = Corner.All) =>
        gui.DrawRectBorder(screenRect, color.ToGui(), thickness, radius, corners);

    /// <inheritdoc cref="Gui.DrawBackgroundRect"/>
    public static Shape DrawBackgroundRect(this Gui gui, Core.Color? color = null,
        float radius = 0.0f, Corner corners = Corner.All) =>
        gui.DrawBackgroundRect(color?.ToGui(), radius, corners);
}
