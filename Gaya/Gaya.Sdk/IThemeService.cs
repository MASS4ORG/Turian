namespace Gaya.Sdk;

/// <summary>
/// Owns which color theme the studio draws with. Themes are <c>.pss</c> sheets found in the host, the user's themes
/// folder, studio bricks or code; the active one is compiled into <see cref="Current"/>. A menu previews a theme
/// while the pointer rests on it and commits the one that is clicked; anything not committed is dropped as soon as
/// the preview stops being renewed.
/// </summary>
public interface IThemeService : IThemeTokenRegistry
{
    /// <summary>Every selectable color theme, built-in ones first.</summary>
    IReadOnlyList<ThemeInfo> ColorThemes { get; }

    /// <summary>The compiled theme currently on screen, preview included.</summary>
    ThemeTokens Current { get; }

    /// <summary>
    /// The id of the committed color theme, which is not the previewed one. It stays the requested id while that
    /// theme is not available yet, such as a brick theme before its brick loads.
    /// </summary>
    string CommittedColorTheme { get; }

    /// <summary>The error that kept the last valid theme on screen, or <c>null</c>.</summary>
    ThemeDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Commits a color theme by id, or by name for settings written before ids, and persists the choice. An id
    /// that is not available yet is remembered and applied once it appears.
    /// </summary>
    /// <param name="id">The theme's id or name.</param>
    void ApplyColorTheme(string id);

    /// <summary>
    /// Shows a color theme without committing it. The preview lasts until the frame after the last call, so a menu
    /// simply renews it while the row is hovered and stops when the pointer leaves.
    /// </summary>
    /// <param name="id">The theme's id or name.</param>
    void PreviewColorTheme(string id);

    /// <summary>Every selectable look, the default one first.</summary>
    IReadOnlyList<ThemeInfo> Looks { get; }

    /// <summary>The id of the committed look; it stays the requested id while that look is not available yet.</summary>
    string CommittedLook { get; }

    /// <summary>
    /// Commits a look by id and persists the choice. The color theme is left alone; see
    /// <see cref="DefaultColorThemeFor"/> to follow the look's own pairing.
    /// </summary>
    /// <param name="id">The look's id.</param>
    void ApplyLook(string id);

    /// <summary>Shows a look without committing it; renewed every frame like <see cref="PreviewColorTheme"/>.</summary>
    /// <param name="id">The look's id.</param>
    void PreviewLook(string id);

    /// <summary>
    /// The color theme a look suggests for the current theme's kind: its dark default when the committed color theme
    /// is dark, its light default otherwise.
    /// </summary>
    /// <param name="lookId">The look's id.</param>
    /// <returns>The suggested theme's id, or <c>null</c> when the look declares none or it is not available.</returns>
    string? DefaultColorThemeFor(string lookId);

    /// <summary>Every selectable icon theme, the default one first.</summary>
    IReadOnlyList<ThemeInfo> IconThemes { get; }

    /// <summary>The id of the committed icon theme; it stays the requested id while that theme is not available yet.</summary>
    string CommittedIconTheme { get; }

    /// <summary>Commits an icon theme by id and persists the choice.</summary>
    /// <param name="id">The icon theme's id.</param>
    void ApplyIconTheme(string id);

    /// <summary>Shows an icon theme without committing it; renewed every frame like <see cref="PreviewColorTheme"/>.</summary>
    /// <param name="id">The icon theme's id.</param>
    void PreviewIconTheme(string id);

    /// <summary>Sets the user's base text size and UI zoom, applied over whatever theme is showing.</summary>
    /// <param name="textSize">Base font size in points.</param>
    /// <param name="zoom">Multiplies every measured length.</param>
    void SetScale(float textSize, float zoom);

    /// <summary>Adds a theme sheet from code. Re-adding an id replaces it.</summary>
    /// <param name="source">The sheet.</param>
    /// <returns>The theme's metadata.</returns>
    ThemeInfo Register(ThemeSource source);

    /// <summary>Adds a theme built from a legacy <see cref="StudioTheme"/>, converted to <c>$token</c> declarations.</summary>
    /// <param name="theme">The theme to offer; its name becomes its id.</param>
    [Obsolete("Register a .pss ThemeSource instead; StudioTheme is removed in the next release.")]
    void Register(StudioTheme theme);

    /// <summary>Raised when the theme on screen changed, preview included.</summary>
    event Action? Changed;
}
