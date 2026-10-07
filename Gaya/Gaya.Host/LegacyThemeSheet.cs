using System.Globalization;

namespace Gaya.Host;

/// <summary>Converts a legacy <see cref="StudioTheme"/> into an equivalent <c>.pss</c> theme sheet.</summary>
[Obsolete("StudioTheme is removed in the next release.")]
static class LegacyThemeSheet
{
    /// <summary>The sheet text: the theme's name as its id, its colors and metrics as tokens over <c>gaya.base</c>.</summary>
    /// <param name="theme">The legacy theme.</param>
    /// <returns>The <c>.pss</c> source.</returns>
    public static string From(StudioTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var name = theme.Name.Replace("\"", "", StringComparison.Ordinal);
        string[] lines =
        [
            $"@const theme-id = \"{name}\";",
            $"@const theme-name = \"{name}\";",
            $"@const theme-kind = {(theme.IsDark ? "dark" : "light")};",
            $"@import \"{ThemeCatalog.BaseId}\";",
            $"$base = {Hex(theme.Panel)};",
            $"$accent = {Hex(theme.Accent)};",
            $"$surface-window = {Hex(theme.Background)};",
            $"$surface-panel = {Hex(theme.Panel)};",
            $"$surface-chrome = {Hex(theme.Chrome)};",
            $"$surface-field = {Hex(theme.Field)};",
            $"$surface-hover = {Hex(theme.Hover)};",
            $"$surface-editor-area = {Hex(theme.EditorArea)};",
            $"$border = {Hex(theme.Border)};",
            $"$ink = {Hex(theme.Ink)};",
            $"$ink-dim = {Hex(theme.InkDim)};",
            $"$ink-faint = {Hex(theme.InkFaint)};",
            $"$accent-fill = {Hex(theme.AccentFill)};",
            $"$error = {Hex(theme.Error)};",
            $"$warning = {Hex(theme.Warning)};",
            $"$folder = {Hex(theme.Folder)};",
            Metric("font-size", theme.FontSize),
            Metric("menu-height", theme.MenuHeight),
            Metric("status-height", theme.StatusHeight),
            Metric("header-height", theme.HeaderHeight),
            Metric("row-height", theme.RowHeight),
            Metric("gap", theme.Gap),
        ];
        return string.Join('\n', lines);
    }

    static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}{color.A:x2}";

    static string Metric(string token, float value) =>
        string.Create(CultureInfo.InvariantCulture, $"${token} = {value};");
}
