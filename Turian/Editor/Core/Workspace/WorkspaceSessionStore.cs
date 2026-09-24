namespace Turian.Editor.Core;

/// <summary>
/// Persists a project's open documents under its <c>.Cache</c> folder, so the session belongs to the
/// project rather than to the machine.
/// </summary>
public sealed class WorkspaceSessionStore(ILogger log)
{
    static readonly JsonSerializerOptions options =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The session file for a project directory.</summary>
    /// <param name="projectDirectory">The project's root directory.</param>
    public static string PathFor(string projectDirectory) =>
        Path.Combine(projectDirectory, ".Cache", "editor-workspace.json");

    /// <summary>Reads the session, or an empty one when there is nothing usable to read.</summary>
    public WorkspaceSession Load(string projectDirectory)
    {
        var path = PathFor(projectDirectory);

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(path), options)
                  ?? WorkspaceSession.Empty
                : WorkspaceSession.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read the workspace session from {Path}", path);
            return WorkspaceSession.Empty;
        }
    }

    /// <summary>Writes the session. A failure is logged, never thrown.</summary>
    public void Save(string projectDirectory, WorkspaceSession session)
    {
        var path = PathFor(projectDirectory);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(session, options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save the workspace session to {Path}", path);
        }
    }
}
