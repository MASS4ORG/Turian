namespace Gaya.Host;

/// <summary>
/// Projects a <see cref="StudioTheme"/> onto the palettes Guinevere's own controls and dock space
/// read, so everything the workbench hosts follows the chosen theme.
/// </summary>
public static class StudioThemeExtensions
{
    /// <summary>The dock space's palette: tab strip, panel fill, splitters and their ink.</summary>
    /// <param name="theme">The theme to project.</param>
    /// <returns>A matching dock theme.</returns>
    public static DockTheme ToDockTheme(this StudioTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        return new DockTheme
        {
            TabStrip = theme.Chrome,
            Panel = theme.Panel,
            Tab = theme.Background,
            Hover = theme.Hover,
            Border = theme.Border,
            Ink = theme.Ink,
            InkDim = theme.InkDim,
            Accent = theme.Accent,
            TabHeight = theme.Scale(theme.HeaderHeight),
            FontSize = theme.Text(theme.FontSize),
            SplitterThickness = theme.Gap,
        };
    }

    /// <summary>
    /// The fallback palette every built-in control draws from — text fields, dropdowns, checkboxes,
    /// scrollbars — so a control that names no colors of its own is themed too.
    /// </summary>
    /// <param name="theme">The theme to project.</param>
    /// <returns>A matching control palette.</returns>
    public static ControlPalette ToControlPalette(this StudioTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        return new ControlPalette
        {
            Surface = theme.Field,
            SurfaceHover = theme.Hover,
            Popup = theme.Panel,
            Border = theme.Border,
            Accent = theme.Accent,
            Text = theme.Ink,
            TextDim = theme.InkDim,
            Selected = theme.Accent,
            Negative = theme.Error,
        };
    }
}
