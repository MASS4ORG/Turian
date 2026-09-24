namespace Gaya.Plugin.Turian;

/// <summary>
/// One Inspector instance's lock and settings buttons, drawn in the width its dock group's tab strip
/// leaves over. Resolves the panel instance through <see cref="IPanelAccessor"/> rather than holding
/// it directly, so it works regardless of whether the tab strip or the panel body resolves first.
/// </summary>
sealed class InspectorTabChrome(
    IPanelAccessor panels,
    IEditorSettings editorSettings,
    InspectorSettings settings,
    string panelId) : IChromeItem
{
    const float menuWidth = 200f;

    bool menuOpen;
    Vector2 menuPosition;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        if (panels.Panel(panelId) is not InspectorPanel inspector) return;

        var theme = StudioTheme.Current;

        using (gui.Node(-1, -1, $"{panelId}/tabMenu").ExpandHeight().Direction(Axis.Horizontal)
                   .Gap(2f).Enter())
        {
            LockButton(gui, theme, inspector);
            SettingsButton(gui, theme);
        }

        // Drawn outside both buttons' own nodes — see OutputPanelChrome for why a popup opened from
        // inside a small, tightly-packed node is misplaced and undecorated.
        gui.Popup(ref menuOpen, () => Preferences(gui, settings, editorSettings),
            width: theme.Scale(menuWidth),
            height: theme.Scale(theme.RowHeight + 8f),
            title: "Inspector",
            position: menuPosition,
            titleBarHeight: theme.Scale(24f),
            backgroundColor: theme.Panel,
            borderColor: theme.Border,
            titleBarColor: theme.Chrome,
            titleTextColor: theme.Ink);
    }

    void LockButton(Gui gui, StudioTheme theme, InspectorPanel inspector)
    {
        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{panelId}/lock")
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText(inspector.Locked ? "🔒" : "🔓", theme.Text(13),
                inspector.Locked ? theme.Accent : hot ? theme.Ink : theme.InkDim);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
                inspector.Locked = !inspector.Locked;
        }
    }

    void SettingsButton(Gui gui, StudioTheme theme)
    {
        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{panelId}/settings")
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

            gui.DrawText("…", StudioTheme.Current.Scale(11f), hot || menuOpen ? theme.Ink : theme.InkDim);

            var anchor = gui.CurrentNode.Rect;
            if (interactable.OnClick())
            {
                menuPosition = TabMenu.Clamp(new Vector2(anchor.X, anchor.Y + anchor.H),
                    theme.Scale(menuWidth), theme.Scale(theme.RowHeight + 32f), gui.RootNode!.Rect);
                menuOpen = true;
            }
        }
    }

    static void Preferences(Gui gui, InspectorSettings settings, IEditorSettings editorSettings)
    {
        var theme = StudioTheme.Current;
        var value = settings.AutoExpandComponents;

        using (gui.Node(-1, theme.Scale(theme.RowHeight)).ExpandWidth().Direction(Axis.Horizontal)
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
