namespace Gaya.Plugin.Turian;

/// <summary>
/// Stands between the user and anything that leaves the open project — exiting, creating or opening another project —
/// asking first whether to save documents with unsaved edits. Asset edits waiting to be saved are written either way.
/// </summary>
sealed class UnsavedChangesGuard(
    AssetWorkspace workspace,
    AssetAutoSave autoSave,
    UnsavedChangesDialogChrome dialog,
    StudioLocalization localization)
{
    /// <summary>Whether anything in the project is not on disk yet.</summary>
    public bool HasUnsavedChanges => workspace.HasUnsavedChanges || autoSave.HasPending;

    /// <summary>Runs <paramref name="proceed"/> once unsaved documents are saved or discarded; cancelling runs nothing.</summary>
    /// <param name="question">The question asked when there are unsaved documents, in English.</param>
    /// <param name="proceed">What leaving the project does.</param>
    public void Leave(string question, Action proceed)
    {
        ArgumentNullException.ThrowIfNull(proceed);

        autoSave.Flush(force: true);
        if (!workspace.HasUnsavedChanges)
        {
            proceed();
            return;
        }

        dialog.Ask(localization.T(question), choice =>
        {
            if (choice == UnsavedChanges.Save) workspace.SaveAll();
            else workspace.CloseAll();
            proceed();
        });
    }
}
