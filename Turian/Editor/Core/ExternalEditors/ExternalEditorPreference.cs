using Gaya.Sdk;
using Gaya.Storage;
using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>
/// Reads and writes the external editor preference without Studio, in the same files and with the same rules Studio
/// uses, so a value set here is what Studio shows and the reverse. The scopes resolve lowest first: Studio, User,
/// Workspace, then Personal, and the highest scope that holds the choice wins.
/// </summary>
public sealed class ExternalEditorPreference
{
    readonly Dictionary<SettingsScope, SettingsFile> files = [];

    /// <summary>Opens the preference of a project.</summary>
    /// <param name="projectDirectory">The project's root folder.</param>
    /// <param name="configDirectory">The per-user folder; <see cref="GayaConfig.Directory"/> when null.</param>
    /// <param name="report">Receives a message and its cause when a file cannot be read or written.</param>
    public ExternalEditorPreference(string projectDirectory, string? configDirectory = null,
        Action<string, Exception>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var project = Path.GetFullPath(projectDirectory);
        var identity = ProjectAsset.Load(new AppSettings { ProjectAbsoluteDir = project }.AssetsAbsoluteDir)?.Id;
        var config = configDirectory ?? GayaConfig.Directory;

        files[SettingsScope.Studio] = new SettingsFile(SettingsPaths.Studio(config), report);
        files[SettingsScope.User] = new SettingsFile(SettingsPaths.User(config), report);
        files[SettingsScope.Workspace] = new SettingsFile(SettingsPaths.Workspace(project), report);
        if (SettingsPaths.Personal(identity?.ToString("D"), SettingsPaths.PersonalRoot(config)) is { } personal)
        {
            files[SettingsScope.Personal] = new SettingsFile(personal, report);
        }
    }

    /// <summary>The scopes this project can store the preference in, lowest first; Personal needs a project identity.</summary>
    public IEnumerable<SettingsScope> Scopes => SettingsScopes.Ascending.Where(files.ContainsKey);

    /// <summary>The file a scope stores the preference in.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The path, or <c>null</c> when the scope is not available for this project.</returns>
    public string? PathFor(SettingsScope scope) => files.TryGetValue(scope, out var file) ? file.Path : null;

    /// <summary>The scopes that hold a choice, lowest first.</summary>
    public IEnumerable<SettingsScope> Holding() => Scopes.Where(scope => Current(scope)!.Has(ExternalEditorSettings.PageId,
        ExternalEditorSettings.Member));

    /// <summary>A scope's file, re-read first when another process, such as Studio, changed it since it was last read.</summary>
    SettingsFile? Current(SettingsScope scope)
    {
        if (!files.TryGetValue(scope, out var file))
        {
            return null;
        }

        if (file.ChangedOnDisk())
        {
            file.Invalidate();
        }

        return file;
    }

    /// <summary>The choice that applies: the one in the highest scope that holds one.</summary>
    /// <returns>The choice and its scope; the default choice and <c>null</c> when no scope holds one.</returns>
    public (ExternalEditorChoice Choice, SettingsScope? Scope) Resolve()
    {
        foreach (var scope in Holding().Reverse())
        {
            if (Read(scope) is { } choice)
            {
                return (choice, scope);
            }
        }

        return (new ExternalEditorChoice(), null);
    }

    /// <summary>Reads what one scope holds, ignoring the others.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The choice, or <c>null</c> when the scope holds none or it cannot be read.</returns>
    public ExternalEditorChoice? Read(SettingsScope scope)
    {
        if (Current(scope)?.Get(ExternalEditorSettings.PageId, ExternalEditorSettings.Member) is not { } node)
        {
            return null;
        }

        try
        {
            return node.Deserialize<ExternalEditorChoice>(SettingsFile.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores a choice in a scope, where it overrides the lower scopes.</summary>
    /// <param name="scope">The scope to write.</param>
    /// <param name="choice">The choice.</param>
    /// <returns><c>true</c> when the file now holds it; <c>false</c> when the scope is unavailable or the file cannot be written.</returns>
    public bool Write(SettingsScope scope, ExternalEditorChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        return files.TryGetValue(scope, out var file) && file.Update(ExternalEditorSettings.PageId,
            new Dictionary<string, JsonNode?>
            {
                [ExternalEditorSettings.Member] = JsonSerializer.SerializeToNode(choice, SettingsFile.JsonOptions),
            }, []);
    }

    /// <summary>Removes the choice from a scope, so the scope below applies.</summary>
    /// <param name="scope">The scope to clear.</param>
    /// <returns><c>true</c> when the scope no longer holds a choice; <c>false</c> when it is unavailable or cannot be written.</returns>
    public bool Clear(SettingsScope scope) =>
        files.TryGetValue(scope, out var file)
        && file.Update(ExternalEditorSettings.PageId, new Dictionary<string, JsonNode?>(),
            [ExternalEditorSettings.Member]);
}
