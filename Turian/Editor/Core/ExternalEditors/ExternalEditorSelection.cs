namespace Turian.Editor.Core;

/// <summary>The editor the user chose: an editor id and the program to start for it.</summary>
/// <param name="EditorId">The id of the <see cref="IExternalEditor"/>, such as <see cref="CustomEditor.EditorId"/>.</param>
/// <param name="Executable">The full path of the program to start.</param>
/// <param name="Arguments">The argument template of a custom editor; <c>null</c> for other editors.</param>
/// <param name="Options">The values of the editor's <see cref="ExternalEditorOption"/>s, by key; <c>null</c> for none.</param>
public sealed record ExternalEditorSelection(
    string EditorId,
    string Executable,
    string? Arguments = null,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>The installation this selection starts.</summary>
    /// <param name="displayName">The editor's name.</param>
    /// <returns>The installation.</returns>
    public EditorInstallation ToInstallation(string displayName) =>
        new(EditorId, displayName, Executable, Arguments: Arguments, Options: Options);
}
