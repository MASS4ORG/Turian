namespace Turian.Editor.Core;

/// <summary>
/// Tells the Studio which locale a project's previews start in. A localization brick's editor assembly
/// implements it, so the editor needs no knowledge of how the project stores that choice.
/// </summary>
public interface IProjectLocalePolicy
{
    /// <summary>The locale a project starts in.</summary>
    /// <param name="settings">The project's settings.</param>
    /// <returns>A BCP-47 locale, or <c>null</c> when the project does not choose one.</returns>
    string? DefaultLocale(IAppSettings settings);
}
