namespace Gaya.Plugin.Turian;

/// <summary>
/// The background-task readout in the status bar. The always-visible row summarises the single most
/// disruptive operation as one aggregate bar, however many nested phases it is made of; clicking it
/// shows the Background Tasks panel, which lists every task and its phases.
/// </summary>
sealed class TaskBarChrome(BackgroundTaskManager tasks, IShellHost? shell = null) : IChromeItem
{
    /// <summary>Row height, also reported to the workbench through the descriptor.</summary>
    public const float Height = 22f;

    const float barWidth = 120f;

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        tasks.Tick();
        var snapshot = tasks.Snapshot();
        if (snapshot.Any(task => !task.IsFinished))
        {
            gui.RequestFrameIn(0.1);
        }

        using (gui.Node(-1, Height).Expand().Direction(Axis.Horizontal).Gap(8).AlignContentY(0.5f).Enter())
        {
            if (snapshot.Count == 0)
            {
                return;
            }

            SummaryRow(gui, snapshot);
        }
    }

    void SummaryRow(Gui gui, IReadOnlyList<BackgroundTask> snapshot)
    {
        var primary = BackgroundTaskTree.Primary(snapshot);
        var activeRoots = BackgroundTaskTree.ActiveRoots(snapshot);

        if (primary is null)
        {
            var finished = BackgroundTaskTree.Roots(snapshot).Count(static t => t.IsFinished);
            Label(gui, finished == 1 ? "1 finished task" : $"{finished} finished tasks", Theme.InkDim);
        }
        else
        {
            var rollup = BackgroundTaskTree.Rollup(snapshot, primary);
            Label(gui, BackgroundTaskTree.Describe(primary, rollup), Theme.Ink);

            // Work that has not reported yet has nothing honest to show, so it animates rather than
            // sitting at an unmoving zero.
            var fraction = rollup.Progress > 0 ? rollup.Progress : (float?)null;
            gui.ProgressBar(fraction).Width(barWidth).Height(5);

            if (activeRoots > 1)
            {
                Label(gui, $"+{activeRoots - 1}", Theme.InkDim);
            }
        }

        if (gui.GetInteractable().OnClick())
        {
            shell?.ShowPanel(GayaPlugin.TasksPanelId);
        }
    }

    static void Label(Gui gui, string text, GuiColor color)
    {
        if (text.Length > 0)
        {
            gui.DrawText(text, Theme.Text(12), color);
        }
    }
}
