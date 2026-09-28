namespace Turian.Editor.Core;

/// <summary>
/// Everything in the open project not on disk yet: documents with unsaved edits and asset edits waiting to be saved.
/// Leaving the project either saves all of it or discards all of it.
/// </summary>
/// <param name="workspace">The open documents.</param>
/// <param name="autoSave">Asset edits waiting to be written.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UnsavedWork(AssetWorkspace workspace, AssetAutoSave autoSave)
{
    /// <summary>Whether anything would be lost by leaving the project now.</summary>
    public bool Any => workspace.HasUnsavedChanges || autoSave.HasPending;

    /// <summary>Writes the pending asset edits and saves every document.</summary>
    public void SaveAll()
    {
        autoSave.Flush(force: true);
        workspace.SaveAll();
    }

    /// <summary>Drops the pending asset edits and closes the documents without saving them.</summary>
    public void DiscardAll()
    {
        autoSave.Discard();
        workspace.CloseAll();
    }
}
