namespace Turian.Editor.Core;

/// <summary>
/// Everything in the open project not on disk yet: documents with unsaved edits and asset edits waiting to be saved.
/// Leaving the project either saves all of it or discards all of it.
/// </summary>
/// <param name="workspace">The open documents.</param>
/// <param name="autoSave">Asset edits waiting to be written.</param>
/// <param name="documents">The document editors, which hold their own unsaved edits.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UnsavedWork(AssetWorkspace workspace, AssetAutoSave autoSave, AssetDocumentHost? documents = null)
{
    /// <summary>Whether anything would be lost by leaving the project now.</summary>
    public bool Any => workspace.HasUnsavedChanges || autoSave.HasPending || documents?.HasUnsavedChanges == true;

    /// <summary>Writes the pending asset edits and saves every document.</summary>
    /// <returns>False when a document could not be written, so leaving must not go on.</returns>
    public bool SaveAll()
    {
        autoSave.Flush(force: true);
        var saved = workspace.SaveAll();
        return (documents?.SaveAll() ?? true) && saved;
    }

    /// <summary>Drops the pending asset edits and closes the documents without saving them.</summary>
    public void DiscardAll()
    {
        autoSave.Discard();
        workspace.CloseAll();
        documents?.DiscardAll();
    }
}
