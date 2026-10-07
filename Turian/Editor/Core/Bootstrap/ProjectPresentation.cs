namespace Turian.Editor.Core;

/// <summary>Resolves the project's display name independently of its directory and compilation name.</summary>
public static class ProjectPresentation
{
    /// <summary>Returns the configured product name, project title, or directory name.</summary>
    /// <param name="project">Project whose settings are already loaded.</param>
    /// <returns>The name displayed in the editor.</returns>
    public static string Name(IAppSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var name = project.Get<PlayerSettings>().ProductName;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        if (!string.IsNullOrWhiteSpace(project.Title)) return project.Title.Trim();
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(project.ProjectAbsoluteDir));
    }

    /// <summary>Reads a recent project's display name without importing its assets.</summary>
    /// <param name="directory">Project directory.</param>
    /// <returns>The configured name, falling back to the directory name.</returns>
    public static string Name(string directory)
    {
        if (SettingsService.Load(directory) is not { } project)
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        ProjectSettingsLoader.LoadFromSources(project);
        return Name(project);
    }
}
