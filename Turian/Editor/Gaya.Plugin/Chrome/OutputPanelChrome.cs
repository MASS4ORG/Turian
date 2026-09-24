namespace Gaya.Plugin.Turian;

/// <summary>
/// The Output console's vertical-dots button, drawn in the width its dock group's tab strip leaves
/// over, and the preferences menu it opens. The host offers that leftover width to this item whenever
/// the Output panel is the group's active tab, which is why the panel itself carries no settings row:
/// the button costs the strip's empty space instead of a row of the panel's body.
/// </summary>
sealed class OutputPanelChrome(
    OutputPanelSettings settings,
    IEditorSettings editorSettings) : IChromeItem
{
    const string buttonId = "gaya.turian.output/tabMenu";
    const float menuWidth = 210f;

    bool menuOpen;
    Vector2 menuPosition;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        var theme = StudioTheme.Current;
        var contentHeight = theme.Scale(7 * theme.RowHeight + 6f + 16f);
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
            title: "Output",
            position: menuPosition,
            titleBarHeight: titleBar,
            backgroundColor: theme.Panel,
            borderColor: theme.Border,
            titleBarColor: theme.Chrome,
            titleTextColor: theme.Ink);
    }

    /// <summary>
    /// The preference rows themselves: the display toggles, the "clear on …" behaviors and the entry
    /// lines stepper. An edit writes into the settings object and reports the page so the value settles
    /// into the user's settings file — the same page the panel reads back.
    /// </summary>
    static void Preferences(Gui gui, OutputPanelSettings settings, IEditorSettings editorSettings)
    {
        var theme = StudioTheme.Current;
        var box = theme.Scale(13f);
        var rowHeight = theme.Scale(theme.RowHeight);
        var size = theme.Text(11f);
        var changed = false;

        using (gui.Node().Direction(Axis.Vertical).Gap(theme.Scale(2f)).Enter())
        {
            changed |= Toggle(gui, "Show Timestamp", box, size, rowHeight,
                () => settings.ShowTimestamp, value => settings.ShowTimestamp = value);
            changed |= Toggle(gui, "Monospace", box, size, rowHeight,
                () => settings.Monospace, value => settings.Monospace = value);
            changed |= Toggle(gui, "Clear on Play", box, size, rowHeight,
                () => settings.ClearOnPlay, value => settings.ClearOnPlay = value);
            changed |= Toggle(gui, "Clear on Build", box, size, rowHeight,
                () => settings.ClearOnBuild, value => settings.ClearOnBuild = value);
            changed |= Toggle(gui, "Clear on Recompile", box, size, rowHeight,
                () => settings.ClearOnRecompile, value => settings.ClearOnRecompile = value);

            using (gui.Node(-1, rowHeight + theme.Scale(6f), "gaya.turian.output/tabMenu/entry")
                       .Direction(Axis.Horizontal).Gap(theme.Scale(6f)).ContentAlignY(0.5f).Enter())
            {
                gui.DrawText("Entry lines", size, theme.InkDim);
                if (StudioControls.SmallTextButton(gui, "−", "gaya.turian.output/tabMenu/minus", theme.Scale(20f)))
                    changed |= StepEntryLines(settings, settings.EntryLines - 1);
                gui.DrawText(settings.EntryLines.ToString(CultureInfo.InvariantCulture), size, theme.Ink);
                if (StudioControls.SmallTextButton(gui, "+", "gaya.turian.output/tabMenu/plus", theme.Scale(20f)))
                    changed |= StepEntryLines(settings, settings.EntryLines + 1);
            }
        }

        if (changed) editorSettings.NotifyChanged(OutputPanelSettings.PageId);
    }

    /// <summary>Draws one checkbox for a settings member and reports whether the click changed it.</summary>
    static bool Toggle(Gui gui, string label, float box, float size, float height,
        Func<bool> get, Action<bool> set)
    {
        using (gui.Node(-1, height).ExpandWidth().Direction(Axis.Horizontal)
                   .ContentAlignY(0.5f).Enter())
        {
            var value = get();
            gui.Checkbox(ref value, label, size: box, fontSize: size, spacing: 6f);
            if (value == get()) return false;

            set(value);
        }

        return true;
    }

    static bool StepEntryLines(OutputPanelSettings settings, int value)
    {
        if (value == settings.EntryLines) return false;

        settings.EntryLines = value;
        return true;
    }
}
