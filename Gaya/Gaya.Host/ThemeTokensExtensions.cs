namespace Gaya.Host;

/// <summary>
/// Projects a <see cref="ThemeTokens"/> snapshot onto the dock space's metrics. Colors reach Guinevere's controls
/// through the theme sheets themselves (see <c>gaya.base.pss</c>).
/// </summary>
public static class ThemeTokensExtensions
{
    /// <summary>The dock space's metrics: tab height, font size and splitter thickness.</summary>
    /// <param name="theme">The theme to project.</param>
    /// <returns>A matching dock theme.</returns>
    public static DockTheme ToDockTheme(this ThemeTokens theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        return new DockTheme
        {
            TabHeight = theme.Scale(theme.HeaderHeight),
            FontSize = theme.Text(theme.FontSize),
            SplitterThickness = theme.Gap,
        };
    }
}
