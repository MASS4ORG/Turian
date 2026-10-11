namespace Gaya.Plugin.Turian;

sealed partial class InspectorPanel
{
    const float headerMenuWidth = 200f;

    bool headerMenuOpen;
    Vector2 headerMenuPosition;

    /// <inheritdoc />
    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        var theme = ThemeTokens.Current;
        var panelId = context.PanelId;

        using (gui.Node(-1, -1, $"{panelId}/tabMenu").ExpandHeight().Direction(Axis.Horizontal)
                   .Gap(2f).Enter())
        {
            HeaderLockButton(gui, theme, panelId);
            HeaderSettingsButton(gui, theme, panelId);
        }

        gui.Popup(ref headerMenuOpen,
            () => HeaderPreferences(gui, settings, context.Services.GetRequiredService<IEditorSettings>()),
            width: theme.Scale(headerMenuWidth),
            height: theme.Scale(theme.RowHeight + 8f),
            title: "Inspector",
            position: headerMenuPosition,
            titleBarHeight: theme.Scale(24f));
    }

    void HeaderLockButton(Gui gui, ThemeTokens theme, string panelId)
    {
        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{panelId}/lock")
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText(Locked ? EditorIcons.Lock : EditorIcons.LockOpen, theme.Text(13),
                Locked ? theme.Accent : hot ? theme.Ink : theme.InkDim);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
                Locked = !Locked;
        }
    }

    void HeaderSettingsButton(Gui gui, ThemeTokens theme, string panelId)
    {
        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{panelId}/settings")
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText("…", ThemeTokens.Current.Scale(11f), hot || headerMenuOpen ? theme.Ink : theme.InkDim);

            var anchor = gui.CurrentNode.Rect;
            if (gui.Pass == Pass.Pass2Render && interactable.OnClick())
            {
                headerMenuPosition = TabMenu.Clamp(new Vector2(anchor.X, anchor.Y + anchor.H),
                    theme.Scale(headerMenuWidth), theme.Scale(theme.RowHeight + 32f), gui.RootNode!.Rect);
                headerMenuOpen = true;
            }
        }
    }

    static void HeaderPreferences(Gui gui, InspectorSettings settings, IEditorSettings editorSettings)
    {
        var theme = ThemeTokens.Current;
        var value = settings.AutoExpandComponents;

        using (gui.Node(-1, theme.Scale(theme.RowHeight), "inspector/preferences").ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Enter())
        {
            gui.Checkbox(ref value, "Auto-Expand Components", size: theme.Scale(13f),
                fontSize: theme.Text(11f), spacing: 6f);

            if (value != settings.AutoExpandComponents)
            {
                settings.AutoExpandComponents = value;
                editorSettings.NotifyChanged("gaya.turian.inspector");
            }
        }
    }
}
