namespace Gaya.Plugin.Turian;

/// <summary>
/// The Asset Browser's vertical-dots button, drawn in the width its dock group's tab strip leaves
/// over, and the preferences menu it opens — the same overflow-menu pattern as the Output console's.
/// The panel itself carries no settings button, so its whole body stays for the tree.
/// </summary>
sealed class AssetBrowserChrome(
    AssetBrowserSettings settings,
    IEditorSettings editorSettings) : IChromeItem
{
    const string buttonId = "gaya.turian.assets/tabMenu";
    const float menuWidth = 180f;

    bool menuOpen;
    Vector2 menuPosition;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        var theme = StudioTheme.Current;
        var contentHeight = theme.Scale(theme.RowHeight + 8f);
        var titleBar = theme.Scale(24f);

        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, buttonId)
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText("…", StudioTheme.Current.Scale(11f), hot || menuOpen ? theme.Ink : theme.InkDim);

            var anchor = gui.CurrentNode.Rect;
            if (interactable.OnClick())
            {
                menuPosition = TabMenu.Clamp(
                    new Vector2(anchor.X, anchor.Y + anchor.H),
                    theme.Scale(menuWidth), contentHeight + titleBar, gui.RootNode!.Rect);
                menuOpen = true;
            }
        }

        // Drawn outside the button's own node: a popup opened from inside a small, tightly-packed
        // node is misplaced and undecorated, the same way one opened from inside a scrolled inspector
        // row is clipped by it. Called every frame, open or not, so its node structure stays stable.
        gui.Popup(ref menuOpen, () => Preferences(gui, settings, editorSettings),
            width: theme.Scale(menuWidth),
            height: contentHeight,
            title: "Assets",
            position: menuPosition,
            titleBarHeight: titleBar,
            backgroundColor: theme.Panel,
            borderColor: theme.Border,
            titleBarColor: theme.Chrome,
            titleTextColor: theme.Ink);
    }

    /// <summary>
    /// The preference rows: today just whether the tree shows file extensions. An edit writes into the
    /// settings object and reports the page, so the value settles into the user's settings file and the
    /// panel rebuilds its tree to match.
    /// </summary>
    static void Preferences(Gui gui, AssetBrowserSettings settings, IEditorSettings editorSettings)
    {
        var theme = StudioTheme.Current;
        var box = theme.Scale(13f);
        var height = theme.Scale(theme.RowHeight);
        var size = theme.Text(11f);
        var value = settings.ShowFileExtensions;
        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Enter())
        {
            gui.Checkbox(ref value, "Show file extensions", size: box, fontSize: size, spacing: 6f);
        }

        if (value == settings.ShowFileExtensions) return;

        settings.ShowFileExtensions = value;
        editorSettings.NotifyChanged(AssetBrowserSettings.PageId);
    }
}
