namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    const float headerMenuWidth = 180f;

    bool headerMenuOpen;
    Vector2 headerMenuPosition;

    /// <inheritdoc />
    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        var theme = ThemeTokens.Current;
        var contentHeight = theme.Scale(theme.RowHeight * 5f + 16f);
        var titleBar = theme.Scale(24f);

        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{context.PanelId}/tabMenu")
                   .ExpandHeight().AlignContentX(0.5f).AlignContentY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot)
            {
                gui.DrawBackgroundRect(theme.Hover);
            }

            gui.DrawText("…", ThemeTokens.Current.Scale(11f), hot || headerMenuOpen ? theme.Ink : theme.InkDim);

            var anchor = gui.CurrentNode.Rect;
            if (gui.Pass == Pass.Pass2Render && interactable.OnClick())
            {
                headerMenuPosition = TabMenu.Clamp(
                    new Vector2(anchor.X, anchor.Y + anchor.H),
                    theme.Scale(headerMenuWidth), contentHeight + titleBar, gui.RootNode!.Rect);
                headerMenuOpen = true;
            }
        }

        // The popup needs the panel's layout scope so it can extend beyond the tab button.
        gui.Popup(ref headerMenuOpen, () =>
        {
            HeaderPreferences(gui, browserSettings, editorSettings);
            if (StudioControls.SmallTextButton(gui, "Settings…", "assets/settings", theme.Scale(150)))
            {
                context.Services.GetService<IShellHost>()?.ShowPanel(ShellPanels.Settings);
            }
        }, title: "Assets", position: headerMenuPosition)
            .Width(theme.Scale(headerMenuWidth)).Height(contentHeight + titleBar);
    }

    /// <summary>
    /// Edits the registered settings page so layout, zoom and label presentation persist together.
    /// </summary>
    static void HeaderPreferences(Gui gui, AssetBrowserSettings settings, IEditorSettings editorSettings)
    {
        var theme = ThemeTokens.Current;
        var height = theme.Scale(theme.RowHeight);
        var value = settings.ShowFileExtensions;
        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal)
                   .AlignContentY(0.5f).Enter())
        {
            gui.Checkbox(ref value, "Show file extensions");
        }

        var favorites = settings.ShowFavoritesInTree;
        gui.Checkbox(ref favorites, "Favorites in tree");
        var mode = settings.ViewMode;
        gui.EnumDropdown(ref mode).Width(theme.Scale(150)).Height(height);
        var zoom = (float)Math.Clamp(settings.GridZoom, 32, 256);
        gui.Slider(ref zoom, 32, 256, step: 16, showValue: true).Width(theme.Scale(150)).Height(height);
        if (value == settings.ShowFileExtensions && favorites == settings.ShowFavoritesInTree
            && mode == settings.ViewMode && (int)zoom == settings.GridZoom)
        {
            return;
        }

        settings.ShowFileExtensions = value;
        settings.ShowFavoritesInTree = favorites;
        settings.ViewMode = mode;
        settings.GridZoom = (int)zoom;
        editorSettings.NotifyChanged(AssetBrowserSettings.PageId);
    }
}
