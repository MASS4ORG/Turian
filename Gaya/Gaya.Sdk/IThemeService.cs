namespace Gaya.Sdk;

/// <summary>
/// Owns which <see cref="StudioTheme"/> the studio draws with. A menu previews a theme while the
/// pointer rests on it and commits the one that is clicked; anything not committed is dropped as soon
/// as the preview stops being renewed.
/// </summary>
public interface IThemeService
{
    /// <summary>Every theme that can be chosen, built-in ones first.</summary>
    IReadOnlyList<StudioTheme> Themes { get; }

    /// <summary>The theme currently on screen, preview included.</summary>
    StudioTheme Current { get; }

    /// <summary>The name of the committed theme, which is not the previewed one.</summary>
    string CommittedName { get; }

    /// <summary>Commits a theme by name and persists the choice. An unknown name is ignored.</summary>
    /// <param name="name">The theme to apply.</param>
    void Apply(string name);

    /// <summary>
    /// Shows a theme without committing it. The preview lasts until the frame after the last call, so
    /// a menu simply renews it while the row is hovered and stops when the pointer leaves.
    /// </summary>
    /// <param name="name">The theme to show.</param>
    void Preview(string name);

    /// <summary>
    /// Sets the user's base text size and UI zoom, applied over whatever theme is showing.
    /// </summary>
    /// <param name="textSize">Base font size in points.</param>
    /// <param name="zoom">Multiplies every measured length.</param>
    void SetScale(float textSize, float zoom);

    /// <summary>Adds a theme beyond the built-in ones. Re-adding a name replaces it.</summary>
    /// <param name="theme">The theme to offer.</param>
    void Register(StudioTheme theme);

    /// <summary>Raised when the theme on screen changed, preview included.</summary>
    event Action? Changed;
}
