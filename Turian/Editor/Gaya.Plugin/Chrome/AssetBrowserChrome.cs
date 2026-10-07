namespace Gaya.Plugin.Turian;

/// <summary>
/// Offers browser layout and display preferences from the asset tab's overflow menu.
/// </summary>
sealed class AssetBrowserChrome(
    AssetBrowserSettings settings,
    IEditorSettings editorSettings,
    IShellHost? shell = null) : IChromeItem
{
    const string buttonId = "gaya.turian.assets/tabMenu";
    const float menuWidth = 180f;

    bool menuOpen;
    Vector2 menuPosition;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        var theme = ThemeTokens.Current;
        var contentHeight = theme.Scale(theme.RowHeight * 5f + 16f);
        var titleBar = theme.Scale(24f);

        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, buttonId)
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText("…", ThemeTokens.Current.Scale(11f), hot || menuOpen ? theme.Ink : theme.InkDim);

            var anchor = gui.CurrentNode.Rect;
            if (gui.Pass == Pass.Pass2Render && interactable.OnClick())
            {
                menuPosition = TabMenu.Clamp(
                    new Vector2(anchor.X, anchor.Y + anchor.H),
                    theme.Scale(menuWidth), contentHeight + titleBar, gui.RootNode!.Rect);
                menuOpen = true;
            }
        }

        // The popup needs the panel's layout scope so it can extend beyond the tab button.
        gui.Popup(ref menuOpen, () =>
        {
            Preferences(gui, settings, editorSettings);
            if (StudioControls.SmallTextButton(gui, "Settings…", "assets/settings", theme.Scale(150)))
                shell?.ShowPanel(ShellPanels.Settings);
        },
            width: theme.Scale(menuWidth),
            height: contentHeight,
            title: "Assets",
            position: menuPosition,
            titleBarHeight: titleBar);
    }

    /// <summary>
    /// Edits the registered settings page so layout, zoom and label presentation persist together.
    /// </summary>
    static void Preferences(Gui gui, AssetBrowserSettings settings, IEditorSettings editorSettings)
    {
        var theme = ThemeTokens.Current;
        var box = theme.Scale(13f);
        var height = theme.Scale(theme.RowHeight);
        var size = theme.Text(11f);
        var value = settings.ShowFileExtensions;
        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Enter())
        {
            gui.Checkbox(ref value, "Show file extensions", size: box, fontSize: size, spacing: 6f);
        }

        var favorites = settings.ShowFavoritesInTree;
        gui.Checkbox(ref favorites, "Favorites in tree", size: box, fontSize: size);
        var mode = settings.ViewMode;
        gui.EnumDropdown(ref mode, width: theme.Scale(150), height: height, fontSize: size);
        var zoom = (float)Math.Clamp(settings.GridZoom, 32, 256);
        gui.Slider(ref zoom, 32, 256, width: theme.Scale(150), height: height, step: 16, showValue: true);
        if (value == settings.ShowFileExtensions && favorites == settings.ShowFavoritesInTree
            && mode == settings.ViewMode && (int)zoom == settings.GridZoom) return;
        settings.ShowFileExtensions = value;
        settings.ShowFavoritesInTree = favorites;
        settings.ViewMode = mode;
        settings.GridZoom = (int)zoom;
        editorSettings.NotifyChanged(AssetBrowserSettings.PageId);
    }
}
