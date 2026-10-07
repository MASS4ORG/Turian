namespace Gaya.Host;

/// <summary>
/// Projects a <see cref="ThemeTokens"/> snapshot onto the palettes Guinevere's own controls and dock space read, so
/// everything the workbench hosts follows the chosen theme.
/// </summary>
public static class ThemeTokensExtensions
{
    /// <summary>The dock space's palette: tab strip, panel fill, splitters and their ink.</summary>
    /// <param name="theme">The theme to project.</param>
    /// <returns>A matching dock theme.</returns>
    public static DockTheme ToDockTheme(this ThemeTokens theme)
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
    public static ControlPalette ToControlPalette(this ThemeTokens theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        // Every color is set: an unset one keeps Guinevere's light default, such as a white scrollbar track.
        return new ControlPalette
        {
            BaseBackground = theme.Panel,
            Surface = theme.Field,
            SurfaceHover = theme.Hover,
            SurfaceActive = theme.Chrome,
            Popup = theme.Popup,
            Border = theme.Border,
            BorderActive = theme.InkFaint,
            Divider = theme.Border,
            Accent = theme.Accent,
            AccentHover = theme.Accent,
            AccentSubtle = theme.AccentFill,
            Text = theme.Ink,
            TextDim = theme.InkDim,
            TextDisabled = theme.InkFaint,
            TextOnAccent = theme.Ink,
            Selected = theme.Accent,
            Negative = theme.Error,
            Warning = theme.Warning,
            FocusRing = theme.FocusRing,
            TextSelection = theme.Selection,
        };
    }
}
