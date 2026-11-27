namespace Gaya.Plugin.Turian;

/// <summary>
/// The open documents as a tab strip directly under the menu bar, spanning the window the way a browser
/// does. Drawn with the same <c>TabStrip</c> the dock space uses, so both look alike. A tab marks unsaved edits with a
/// dot, a read-only source with a lock and a conflicting one with a warning.
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

        var active = workspace.Active?.Id;
        var strip = gui.TabStrip(ref active, BuildTabs, id: "documents");
        strip.Height(Height);
        if (strip.Changed && active is not null && workspace.Find(active) is { } activated)
        {
            workspace.Activate(activated);
        }

        void BuildTabs(TabBuilder tabs)
        {
            foreach (var document in workspace.Documents)
            {
                tabs.Tab(TitleOf(gui, document), closable: true, key: document.Id,
                    modified: document.IsDirty, onClose: () => Close(document));
            }
        }
    }

    /// <summary>The theme icons a tab shows ahead of its title, a conflict before a read-only source.</summary>
    /// <param name="document">The tab.</param>
    public static IEnumerable<string> Markers(AssetWrapper document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Conflict is not null)
        {
            yield return Icons.LogWarning;
        }

        if (document.IsReadOnly)
        {
            yield return Icons.Lock;
        }
    }

    static Content TitleOf(Gui gui, AssetWrapper document)
    {
        Content[] markers = [.. Markers(document).Select(id => (Content)gui.ResolveIcon(id))];
        return markers.Length == 0
            ? document.Title
            : Content.Row([.. markers, document.Title]).Labeled(document.Title);
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
