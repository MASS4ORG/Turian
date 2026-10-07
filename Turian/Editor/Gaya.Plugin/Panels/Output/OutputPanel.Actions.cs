namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    bool menuOpen;
    Vector2 menuAt;
    LogLine? contextLine;

    void HandleRowInput(Gui gui, InteractableElement interaction, LogRow row, int index)
    {
        if (interaction.OnClick(MouseButton.Right))
        {
            selectedIndex = index;
            contextLine = row.Line;
            menuAt = gui.Input.MousePosition;
            menuOpen = true;
        }
        if (!interaction.OnClick(out var clicks)) return;
        selectedIndex = index;
        if (clicks >= 2) OpenSource(row.Line);
    }

    void BuildContextMenu(FlyoutBuilder menu, Gui gui)
    {
        if (contextLine is not { } line) return;
        menu.Item(localization.T("Open"), () => OpenSource(line),
            enabled: LogSource.ResolvePath(LogSource.LocationMessage(line),
                settingsService.Settings?.ProjectAbsoluteDir) is not null);
        menu.Item(localization.T("Copy Line"), () => gui.Input.SetClipboardText(line.Text.Split('\n')[0].TrimEnd('\r')));
        menu.Item(localization.T("Copy All"), () => gui.Input.SetClipboardText(
            string.Join(Environment.NewLine, rows.Select(row => row.Line.Text))));
    }

    void OpenSource(LogLine line) =>
        LogSource.Open(LogSource.LocationMessage(line), log, settingsService.Settings?.ProjectAbsoluteDir);
}
