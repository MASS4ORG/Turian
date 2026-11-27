namespace Turian.Editor.Core;

/// <summary>
/// A document the workspace keeps open as a tab. The scene, prefab and asset tabs implement it, and an editor brick
/// implements it for the sources it edits itself; disposing it closes it.
/// </summary>
public interface IWorkspaceDocument : IDisposable
{
    /// <summary>The asset id of the source, as a GUID string; one document is open per id.</summary>
    string Id { get; }

    /// <summary>The id of the document's kind, which decides its layout and panels.</summary>
    string Kind { get; }

    /// <summary>The name shown in the tab.</summary>
    string Title { get; }

    /// <summary>The absolute path of the source file, or null when the document has none in the project.</summary>
    string? SourcePath { get; }

    /// <summary>Whether there are edits that are not written to the source yet.</summary>
    bool IsDirty { get; }

    /// <summary>Whether the source may not be written, such as one inside an installed brick.</summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Why the unsaved edits and the source disagree, such as a file changed on disk while dirty; null when they do
    /// not. While set, saving is refused unless it overwrites.
    /// </summary>
    string? Conflict { get; }

    /// <summary>Raised when the title, dirty state, read-only state or conflict may have changed.</summary>
    event Action? Changed;

    /// <summary>
    /// Writes the edits to the source. A failure leaves the document dirty. The workspace calls it only for a dirty,
    /// writable document, and for a conflicted one only to overwrite.
    /// </summary>
    /// <param name="overwrite">Whether to replace a source that conflicts with the edits.</param>
    /// <returns>Whether the source was written, and why not.</returns>
    DocumentSaveResult Save(bool overwrite = false);

    /// <summary>Drops the unsaved edits and reads the source again, clearing any conflict.</summary>
    void Reload();
}
