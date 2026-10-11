namespace Gaya.Sdk;

/// <summary>Where a theme sheet was found; later origins win when two sheets declare the same id.</summary>
public enum ThemeOrigin
{
    /// <summary>Embedded in the host.</summary>
    BuiltIn,
    /// <summary>A <c>.pss</c> file in the user's themes folder.</summary>
    User,
    /// <summary>A studio-scope brick.</summary>
    Brick,
    /// <summary>Registered from code through <see cref="IThemeService.Register(ThemeSource)"/>.</summary>
    Code,
}

/// <summary>One selectable theme sheet, read from its <c>@const</c> metadata.</summary>
/// <param name="Id">The stable <c>theme-id</c> persisted in the settings.</param>
/// <param name="Name">The <c>theme-name</c> shown in menus.</param>
/// <param name="Kind">The <c>theme-kind</c>.</param>
/// <param name="Category">What the sheet provides, such as <see cref="ThemeCategories.ColorTheme"/>.</param>
/// <param name="Origin">Where the sheet was found.</param>
/// <param name="Path">The sheet's file, or <c>null</c> for embedded and code sheets.</param>
public sealed record ThemeInfo(string Id, string Name, ThemeKind Kind, string Category, ThemeOrigin Origin,
    string? Path);

/// <summary>The brick categories a theme sheet can belong to.</summary>
public static class ThemeCategories
{
    /// <summary>A color theme: surfaces, ink, accents and metrics.</summary>
    public const string ColorTheme = "gaya:theme";

    /// <summary>An icon theme mapping icon ids to glyphs or images.</summary>
    public const string IconTheme = "gaya:icon-theme";

    /// <summary>A font pack declaring <c>@font-face</c> entries.</summary>
    public const string FontPack = "gaya:font-pack";
}

/// <summary>A theme sheet supplied from code instead of a file.</summary>
/// <param name="Text">The <c>.pss</c> source, declaring at least <c>@const theme-id</c> and <c>theme-name</c>.</param>
/// <param name="SourceName">Name reported in parse errors; defaults to the theme id.</param>
public sealed record ThemeSource(string Text, string? SourceName = null);

/// <summary>A located theme parse or evaluation error.</summary>
/// <param name="Message">The error, prefixed with <c>file:line:column</c> when the position is known.</param>
/// <param name="Source">The failing sheet's file or source name.</param>
/// <param name="Line">1-based line, or 0 when unknown.</param>
/// <param name="Column">1-based column, or 0 when unknown.</param>
public sealed record ThemeDiagnostic(string Message, string? Source, int Line, int Column);

/// <summary>
/// Theme tokens a plugin declares with defaults, so its own drawing follows the theme while any theme can override
/// them by name.
/// </summary>
public interface IThemeTokenRegistry
{
    /// <summary>Declares a token and its default <c>.pss</c> value, which may reference other tokens.</summary>
    /// <param name="name">A name prefixed with the plugin's own, such as <c>acme-glow</c>, without <c>$</c>.</param>
    /// <param name="defaultValue">The value used when the active theme does not set the token.</param>
    void Token(string name, string defaultValue);
}
