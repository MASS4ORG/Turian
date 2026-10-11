namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    const float headerMenuWidth = 210f;

    bool headerMenuOpen;
    Vector2 headerMenuPosition;

    /// <inheritdoc />
    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        var theme = ThemeTokens.Current;
        var contentHeight = theme.Scale(7 * theme.RowHeight + 6f + 16f);
        var titleBar = theme.Scale(24f);

        using (gui.Node(theme.Scale(theme.HeaderHeight), -1, $"{context.PanelId}/tabMenu")
                   .ExpandHeight().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render && hot) gui.DrawBackgroundRect(theme.Hover);

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

        gui.Popup(ref headerMenuOpen,
            () => HeaderPreferences(gui, settings, context.Services.GetRequiredService<IEditorSettings>(), localization),
            width: theme.Scale(headerMenuWidth),
            height: contentHeight,
            title: localization.T("Output"),
            position: headerMenuPosition,
            titleBarHeight: titleBar);
    }

    /// <summary>
    /// The preference rows themselves: the display toggles, the "clear on …" behaviors and the entry
    /// lines stepper. An edit writes into the settings object and reports the page so the value settles
    /// into the user's settings file — the same page the panel reads back.
    /// </summary>
    static void HeaderPreferences(Gui gui, OutputPanelSettings settings, IEditorSettings editorSettings,
        StudioLocalization localization)
    {
        var theme = ThemeTokens.Current;
        var box = theme.Scale(13f);
        var rowHeight = theme.Scale(theme.RowHeight);
        var size = theme.Text(11f);
        var changed = false;

        using (gui.Node().Direction(Axis.Vertical).Gap(theme.Scale(2f)).Enter())
        {
            changed |= Toggle(gui, "showTimestamp", localization.T("Show Timestamp"), box, size, rowHeight,
                () => settings.ShowTimestamp, value => settings.ShowTimestamp = value);
            changed |= Toggle(gui, "monospace", localization.T("Monospace"), box, size, rowHeight,
                () => settings.Monospace, value => settings.Monospace = value);
            changed |= Toggle(gui, "clearOnPlay", localization.T("Clear on Play"), box, size, rowHeight,
                () => settings.ClearOnPlay, value => settings.ClearOnPlay = value);
            changed |= Toggle(gui, "clearOnBuild", localization.T("Clear on Build"), box, size, rowHeight,
                () => settings.ClearOnBuild, value => settings.ClearOnBuild = value);
            changed |= Toggle(gui, "clearOnRecompile", localization.T("Clear on Recompile"), box, size, rowHeight,
                () => settings.ClearOnRecompile, value => settings.ClearOnRecompile = value);

            using (gui.Node(-1, rowHeight + theme.Scale(6f), "gaya.turian.output/tabMenu/entry")
                       .Direction(Axis.Horizontal).Gap(theme.Scale(6f)).ContentAlignY(0.5f).Enter())
            {
                gui.DrawText(localization.T("Entry lines"), size, theme.InkDim);
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
    static bool Toggle(Gui gui, string id, string label, float box, float size, float height,
        Func<bool> get, Action<bool> set)
    {
        using (gui.Node(-1, height, $"output/preferences/{id}").ExpandWidth().Direction(Axis.Horizontal)
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
