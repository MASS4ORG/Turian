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
            if (choice == UnsavedChanges.Save) unsaved.SaveAll();
            else unsaved.DiscardAll();
            proceed();
        });
    }
}
