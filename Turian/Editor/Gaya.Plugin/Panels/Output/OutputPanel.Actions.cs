namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    bool menuOpen;
    Vector2 menuAt;
    LogLine? contextLine;
    string contextCopy = "";

    void HandleRowInput(Gui gui, InteractableElement interaction, LogRow row, int index)
    {
        if (interaction.OnClick(MouseButton.Right))
        {
            if (!selection.Contains(row))
            {
                selection.Set(rows, index);
            }

            selectedIndex = index;
            OpenMenu(gui, row.Line, selection.Text(rows));
        }
        if (!interaction.OnClick(out var clicks))
        {
            return;
        }

        Select(gui, index);
        if (clicks >= 2)
        {
            OpenSource(row.Line);
        }
    }

    /// <summary>
    /// Applies a plain click, Ctrl+click (toggle) or Shift+click (range from the anchor, added to the selection
    /// with Ctrl) to the clicked row.
    /// </summary>
    void Select(Gui gui, int index)
    {
        if (Shift(gui))
        {
            selection.Extend(rows, index, additive: Control(gui));
        }
        else if (Control(gui))
        {
            selection.Toggle(rows, index);
        }
        else
        {
            selection.Set(rows, index);
        }

        selectedIndex = selection.Contains(rows[index]) ? index : selection.LastIndex(rows);
    }

    /// <summary>
    /// Ctrl+A selects every entry; Ctrl+C copies the text highlighted in the detail pane, or the whole text of
    /// every selected entry when nothing is highlighted.
    /// </summary>
    bool HandleClipboardKeys(Gui gui)
    {
        if (!Control(gui))
        {
            return false;
        }

        if (gui.Input.IsKeyPressed(KeyboardKey.A))
        {
            selection.SelectAll(rows);
            selectedIndex = rows.Count - 1;
            return true;
        }

        if (gui.Input.IsKeyPressed(KeyboardKey.C))
        {
            gui.Input.SetClipboardText(CopyText());
            return true;
        }

        return false;
    }

    string CopyText() => detailState is { HasSelection: true } highlighted
        ? highlighted.SelectedText
        : selection.Text(rows);

    void OpenMenu(Gui gui, LogLine line, string copy)
    {
        contextLine = line;
        contextCopy = copy;
        menuAt = gui.Input.MousePosition;
        menuOpen = true;
    }

    void BuildContextMenu(FlyoutBuilder menu, Gui gui)
    {
        if (contextLine is not { } line)
        {
            return;
        }

        menu.Item(localization.T("Open"), () => OpenSource(line),
            enabled: LogSource.ResolvePath(LogSource.LocationMessage(line),
                settingsService.Settings?.ProjectAbsoluteDir) is not null);
        menu.Item(localization.T("Copy"), () => gui.Input.SetClipboardText(contextCopy));
        menu.Item(localization.T("Copy Line"), () => gui.Input.SetClipboardText(line.Text.Split('\n')[0].TrimEnd('\r')));
        menu.Item(localization.T("Copy All"), () => gui.Input.SetClipboardText(
            string.Join(Environment.NewLine, rows.Select(row => row.Line.Text))));
    }

    void OpenSource(LogLine line) =>
        LogSource.Open(LogSource.LocationMessage(line), log, settingsService.Settings?.ProjectAbsoluteDir);

    static bool Control(Gui gui) =>
        gui.Input.IsKeyDown(KeyboardKey.LeftControl) || gui.Input.IsKeyDown(KeyboardKey.RightControl);

    static bool Shift(Gui gui) =>
        gui.Input.IsKeyDown(KeyboardKey.LeftShift) || gui.Input.IsKeyDown(KeyboardKey.RightShift);
}
