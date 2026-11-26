namespace Gaya.Plugin.Turian;

/// <summary>
/// Stands between the user and anything that leaves the open project — exiting, creating or opening another project —
/// asking first whether to save the unsaved work.
/// </summary>
sealed class UnsavedChangesGuard(
    UnsavedWork unsaved,
    UnsavedChangesDialogChrome dialog,
    StudioLocalization localization)
{
    /// <summary>Whether anything in the project is not on disk yet.</summary>
    public bool HasUnsavedChanges => unsaved.Any;

    /// <summary>Asks whether to save, discard or keep open one document editor's unsaved edits.</summary>
    /// <param name="session">The document being closed; named in no way beyond the question.</param>
    /// <param name="decided">Runs with Save or Discard.</param>
    /// <param name="cancelled">Runs when the user cancels or dismisses the question.</param>
    public void AskClose(IAssetDocumentSession session, Action<UnsavedChanges> decided, Action cancelled) =>
        dialog.Ask(localization.T("This document has unsaved changes. Save them first?"), decided, cancelled);

    /// <summary>Runs <paramref name="proceed"/> once unsaved work is saved or discarded; Cancel runs nothing.</summary>
    /// <param name="question">The question asked when there is unsaved work, in English.</param>
    /// <param name="proceed">What leaving the project does.</param>
    public void Leave(string question, Action proceed)
    {
        ArgumentNullException.ThrowIfNull(proceed);

        if (!unsaved.Any)
        {
            proceed();
            return;
        }

        dialog.Ask(localization.T(question), choice =>
        {
            if (choice == UnsavedChanges.Save)
            {
                // A document that could not be written stays open and dirty, so the project is not left.
                if (!unsaved.SaveAll())
                {
                    return;
                }
            }
            else
            {
                unsaved.DiscardAll();
            }

            proceed();
        });
    }
}
