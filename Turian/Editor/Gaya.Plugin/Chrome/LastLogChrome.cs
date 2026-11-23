namespace Gaya.Plugin.Turian;

/// <summary>
/// The status bar's latest-message readout: the newest Output entry's severity icon and first line. Clicking it
/// shows the Output panel and selects that entry there.
/// </summary>
sealed class LastLogChrome(OutputRevealService requests, IShellHost shell) : IChromeItem
{
    string shownText = "";
    string firstLine = "";

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        using (gui.Node(-1, -1, "statusbar/lastLog").Expand().Direction(Axis.Horizontal).Gap(6f).Padding(8f, 0f)
                   .AlignContentY(0.5f).Enter())
        {
            if (LogBuffer.Latest is not { } line)
            {
                return;
            }

            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();
            if (gui.Pass == Pass.Pass2Render && hot)
            {
                gui.DrawBackgroundRect(Theme.Hover, 3f);
            }

            _ = gui.ThemedIcon(OutputPanel.LevelIcon(line.Level), Theme.Scale(14f));
            _ = gui.DrawText(FirstLine(line.Text), Theme.Text(12), hot ? Theme.Ink : Theme.InkDim,
                centerInRect: false, clip: true).ExpandWidth();

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
            {
                requests.Request(line);
                shell.ShowPanel(GayaPlugin.OutputPanelId);
            }
        }
    }

    /// <summary>The message's first line, split once per new message rather than every frame.</summary>
    string FirstLine(string text)
    {
        if (!ReferenceEquals(text, shownText))
        {
            shownText = text;
            firstLine = text.Split('\n')[0].TrimEnd('\r');
        }

        return firstLine;
    }
}
