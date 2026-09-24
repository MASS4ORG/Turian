namespace Turian.Engine.Core;

/// <summary>
/// An open project. A project is a folder holding an <c>Assets</c> folder and nothing else is required:
/// everything it configures lives in settings assets under <c>Assets</c> — one per aspect, such as
/// <see cref="PlayerSettings"/> or <see cref="GraphicsSettings"/> — read through <see cref="Get{T}"/>.
/// </summary>
public interface IAppSettings
{
    /// <summary>The project folder, or an empty string when no project is open.</summary>
    string ProjectAbsoluteDir { get; set; }

    /// <summary>The project's <c>Assets</c> folder, or an empty string when no project is open.</summary>
    string AssetsAbsoluteDir { get; }

    /// <summary>
    /// The project's name: its folder name, which is also what its scripts' assembly is called. What
    /// players see is <see cref="PlayerSettings.ProductName"/>.
    /// </summary>
    string? Title { get; set; }

    /// <summary>The settings assets read for this project, filled by <see cref="ProjectSettingsLoader"/>.</summary>
    ProjectSettingsSet Loaded { get; }

    /// <summary>The settings of one kind, or its declared defaults when the project has none.</summary>
    /// <typeparam name="T">The settings kind.</typeparam>
    /// <returns>The settings.</returns>
    T Get<T>()
        where T : ProjectSettingsAsset, new();

    /// <summary>
    /// Convert the Tile to use only path-friendly characters
    /// </summary>
    string? TitleToPathFriendly { get; }

    /// <summary>
    /// Essentially to clone or absorb settings from other object.
    /// </summary>
    /// <param name="appSettings"></param>
    /// <returns></returns>
    IAppSettings Load(IAppSettings appSettings);
}
