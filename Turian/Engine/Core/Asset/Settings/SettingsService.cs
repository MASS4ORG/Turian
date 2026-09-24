namespace Turian.Engine.Core;

/// <summary>
/// Holds the open project and tells whoever is listening when another one is opened.
/// </summary>
public class SettingsService
{
    /// <summary>The folder every project keeps its assets in.</summary>
    public const string AssetsFolderName = "Assets";

    /// <summary>
    /// The file older projects kept at their root. It is still accepted wherever a project path is, and
    /// names that project's folder.
    /// </summary>
    public const string LegacyProjectFileName = "project.data";

    /// <summary>
    /// Raised when the settings are successfully loaded.
    /// </summary>
    public event Action<AppSettings>? SettingsLoaded;

    /// <summary>
    /// Holds the current loaded settings.
    /// </summary>
    public AppSettings? Settings { get; private set; }

    /// <summary>
    /// The project at a path: a project folder, or an older project's <c>project.data</c> naming it. The
    /// settings assets are not read; <see cref="ProjectSettingsLoader"/> does that.
    /// </summary>
    /// <param name="path">A project folder, or a file inside one.</param>
    /// <returns>The project, or null when the path is not one.</returns>
    public static AppSettings? Load(string path)
    {
        if (ResolveProjectDirectory(path) is not { } directory) return null;

        return new AppSettings
        {
            ProjectAbsoluteDir = directory,
            Title = Path.GetFileName(directory),
        };
    }

    /// <summary>
    /// Sets the loaded settings and triggers the event.
    /// </summary>
    public AppSettings Set(AppSettings settings)
    {
        Settings = settings;
        SettingsLoaded?.Invoke(Settings);
        return Settings;
    }

    /// <summary>Whether a path names a project: a folder holding <c>Assets</c>, or a file inside one.</summary>
    /// <param name="path">The path to test.</param>
    /// <returns>True for a project.</returns>
    public static bool IsValid(string path) => ResolveProjectDirectory(path) is not null;

    /// <summary>
    /// The project folder a path names — the folder itself, or the folder a file such as an older
    /// <c>project.data</c> sits in — provided it holds an <c>Assets</c> folder.
    /// </summary>
    /// <param name="path">A folder or a file path.</param>
    /// <returns>The absolute project folder, or null when the path is not a project.</returns>
    public static string? ResolveProjectDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var full = Path.GetFullPath(path);
        var directory = Directory.Exists(full)
            ? full
            : File.Exists(full) ? Path.GetDirectoryName(full) : null;

        directory = directory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return directory is { Length: > 0 } && Directory.Exists(Path.Combine(directory, AssetsFolderName))
            ? directory
            : null;
    }

    /// <summary>
    /// Indicates whether the settings have been loaded.
    /// </summary>
    public bool HasSettings => Settings != null;
}
