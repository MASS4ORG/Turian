namespace Gaya.Plugin.Turian;

/// <summary>
/// The open assets as a tab strip directly under the menu bar, spanning the window the way a browser
/// does. Drawn with the same <c>TabStrip</c> the dock space uses, so both look alike.
/// </summary>
sealed class DocumentTabsChrome(
    AssetWorkspace workspace,
    UnsavedChangesDialogChrome unsavedChanges,
    StudioLocalization localization) : IChromeItem
{
    /// <summary>Strip height, also reported to the workbench through the descriptor.</summary>
    public const float Height = 26f;


    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (workspace.Documents.Count == 0)
        {
            return;
        }

        var tabs = workspace.Documents
            .Where(document => document.Asset is not null)
            .Select(document => new TabStripItem(
                document.Asset!.Id.ToString(), document.Title, Modified: document.IsDirty, Tag: document))
            .ToList();

        var active = workspace.Active?.Asset?.Id.ToString();
        var outcome = gui.TabStrip(tabs, active, StudioControls.Tabs(Height), "documents");

        if (outcome.Activated?.Tag is AssetWrapper activated)
            workspace.Activate(activated);

        if (outcome.Closed?.Tag is not AssetWrapper closed) return;

        if (!closed.IsDirty)
        {
            workspace.Close(closed);
            return;
        }

        var question = string.Format(CultureInfo.CurrentCulture,
            localization.T("{0} has unsaved changes. Save them before closing?"), closed.Title);
        unsavedChanges.Ask(question, choice => workspace.Close(closed, choice));
    }
}
