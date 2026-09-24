namespace Gaya.Plugin.Turian;

/// <summary>
/// Geometry shared by the panels' tab menus: where a menu may open so it stays fully on the stage,
/// and the … glyph itself. Panel-specific menus pick their own width and height and anchor them at
/// their button, then run the result through <see cref="Clamp"/>.
/// </summary>
public static class TabMenu
{
    const float inset = 4f;

    /// <summary>
    /// Shifts a menu's top-left corner so the whole menu stays inside the stage. The button that opens
    /// a tab menu sits on the right edge of a tab strip, so without this the menu would hang off the
    /// window's edge — only its left column visible.
    /// </summary>
    /// <param name="position">The corner the menu was anchored at.</param>
    /// <param name="width">The menu's width.</param>
    /// <param name="height">The menu's full height, padding and title bar included.</param>
    /// <param name="stage">The stage the menu must stay inside.</param>
    /// <returns>The adjusted corner.</returns>
    public static Vector2 Clamp(Vector2 position, float width, float height, Rect stage)
    {

        var minX = stage.X + inset;
        var minY = stage.Y + inset;
        var maxX = Math.Max(minX, stage.X + stage.W - width - inset);
        var maxY = Math.Max(minY, stage.Y + stage.H - height - inset);

        return new Vector2(Math.Clamp(position.X, minX, maxX), Math.Clamp(position.Y, minY, maxY));
    }
}
