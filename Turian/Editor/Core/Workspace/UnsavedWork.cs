namespace Turian.Editor.Core;

/// <summary>
/// Everything in the open project not on disk yet: documents with unsaved edits and asset edits waiting to be saved.
/// Leaving the project either saves all of it or discards all of it, and anything that reads the project from disk
/// saves all of it first.
/// </summary>
/// <param name="workspace">The open documents.</param>
/// <param name="autoSave">Asset edits waiting to be written.</param>
/// <param name="notifications">Where the user is told that something could not be started.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UnsavedWork(AssetWorkspace workspace, AssetAutoSave autoSave,
    EditorNotifications? notifications = null)
{
    /// <summary>Whether anything would be lost by leaving the project now.</summary>
    public bool Any => workspace.HasUnsavedChanges || autoSave.HasPending;

    /// <summary>Writes the pending asset edits and saves every document.</summary>
    /// <returns>False when a document could not be written, so leaving must not go on.</returns>
    public bool SaveAll()
    {
        var written = autoSave.Flush(force: true);
        return workspace.SaveAll() && written;
    }

    /// <summary>
    /// Saves everything before something reads the project from disk, such as Play or an export, and tells the user
    /// what is still unsaved when that fails.
    /// </summary>
    /// <param name="action">What is about to start, completing the sentence "Cannot …".</param>
    /// <returns>False when a document is still unsaved, so the action must not start.</returns>
    public bool SaveBefore(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (SaveAll())
        {
            return true;
        }

        var unsaved = workspace.Documents.Where(static document => document.IsDirty).Select(static d => d.Title)
            .Concat(autoSave.Rejected.Select(static path => Path.GetFileName(path)));
        notifications?.Post(new EditorNotification(
            $"Cannot {action}: these documents are not saved: {string.Join(", ", unsaved)}.", IsError: true));
        return false;
    }

    /// <summary>Drops the pending asset edits and closes the documents without saving them.</summary>
    public void DiscardAll()
    {
        autoSave.Discard();
        workspace.CloseAll();
    }
}
