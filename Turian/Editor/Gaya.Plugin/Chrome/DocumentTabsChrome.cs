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

        var active = workspace.Active?.Asset?.Id.ToString();
        var strip = gui.TabStrip(ref active, BuildTabs, id: "documents");
        strip.Height(Height);
        if (strip.Changed && workspace.Documents.FirstOrDefault(document =>
                document.Asset?.Id.ToString() == active) is { } activated)
        {
            workspace.Activate(activated);
        }

        void BuildTabs(TabBuilder tabs)
        {
            foreach (var document in workspace.Documents)
            {
                if (document.Asset is { } asset)
                {
                    tabs.Tab(document.Title, closable: true, key: asset.Id.ToString(),
                        modified: document.IsDirty, onClose: () => Close(document));
                }
            }
        }
    }

    void Close(AssetWrapper closed)
    {
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
