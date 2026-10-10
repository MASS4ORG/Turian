namespace Gaya.Plugin.Turian;

/// <summary>Draws the studio's themed icons, so panels swap icon sets with the theme and never carry a glyph.</summary>
static class StudioIcons
{
    extension(Gui gui)
    {
        /// <summary>
        /// Lays out the active icon theme's icon as a square node. Fading it keeps the theme's own color, which a
        /// tint would replace; a missing id still reserves the square.
        /// </summary>
        /// <param name="id">An id from <see cref="Icons"/> or a plugin's own.</param>
        /// <param name="size">The side of the square.</param>
        /// <param name="opacity">Fades the icon, such as for a disabled or unselected control.</param>
        /// <param name="tint">Overrides the theme's color for single-color icons.</param>
        /// <returns>The node the icon occupies.</returns>
        public LayoutNode ThemedIcon(string id, float size, float opacity = 1f, GuiColor? tint = null) =>
            gui.Icon(gui.ResolveIcon(id), size, tint, opacity);
    }
}
