namespace Gaya.Storage;

/// <summary>Where each settings scope keeps its file, so the studio and headless tools always agree.</summary>
public static class SettingsPaths
{
    /// <summary>The file name the user, workspace and personal scopes use, each in its own folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>The file name of the Studio scope, in the per-user folder.</summary>
    public const string StudioFileName = "studio.json";

    /// <summary>The project sub-folder a workspace's settings live in.</summary>
    public const string WorkspaceDirectory = ".gaya";

    /// <summary>The per-user sub-folder holding one folder of personal values per project.</summary>
    public const string PersonalDirectory = "projects";

    /// <summary>The Studio scope's file.</summary>
    /// <param name="configDirectory">The per-user folder.</param>
    public static string Studio(string configDirectory) => Path.Combine(configDirectory, StudioFileName);

    /// <summary>The user scope's file.</summary>
    /// <param name="configDirectory">The per-user folder.</param>
    public static string User(string configDirectory) => Path.Combine(configDirectory, FileName);

    /// <summary>The workspace scope's file, inside the project.</summary>
    /// <param name="projectDirectory">The project's root folder.</param>
    public static string Workspace(string projectDirectory) =>
        Path.Combine(projectDirectory, WorkspaceDirectory, FileName);

    /// <summary>The folder holding one folder of personal values per project.</summary>
    /// <param name="configDirectory">The per-user folder.</param>
    public static string PersonalRoot(string configDirectory) => Path.Combine(configDirectory, PersonalDirectory);

    /// <summary>
    /// The personal scope's file for a project. Only a GUID names a folder, in its canonical lower-case form, so an
    /// identity cannot escape its folder and one project never splits across spellings.
    /// </summary>
    /// <param name="projectId">The project's identity.</param>
    /// <param name="personalRoot">The folder holding the per-project folders, see <see cref="PersonalRoot"/>.</param>
    /// <returns>The path, or <c>null</c> when <paramref name="projectId"/> is not a GUID.</returns>
    public static string? Personal(string? projectId, string personalRoot) =>
        Guid.TryParse(projectId, out var id) ? Path.Combine(personalRoot, id.ToString("D"), FileName) : null;
}
