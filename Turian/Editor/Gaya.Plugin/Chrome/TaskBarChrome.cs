namespace Gaya.Plugin.Turian;

/// <summary>
/// The background-task readout in the status bar. The always-visible row summarises the single most
/// disruptive operation as one aggregate bar, however many nested phases it is made of; clicking it
/// opens the full list, including each task's phases.
/// </summary>
sealed class TaskBarChrome(BackgroundTaskManager tasks) : IChromeItem
{
    /// <summary>Row height, also reported to the workbench through the descriptor.</summary>
    public const float Height = 22f;

    const float barWidth = 120f;
    const float popupWidth = 460f;
    const float popupHeight = 220f;

    static StudioTheme Theme => StudioTheme.Current;


    bool listOpen;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        tasks.Tick();
        var snapshot = tasks.Snapshot();

        using (gui.Node(-1, Height).Expand().Direction(Axis.Horizontal).Gap(8).ContentAlignY(0.5f).Enter())
        {
            if (snapshot.Count == 0)
            {
                listOpen = false;
                return;
            }

            SummaryRow(gui, snapshot);
        }

        if (listOpen)
        {
            gui.Popup(ref listOpen, () => TaskList(gui), popupWidth, popupHeight, "Background tasks",
                new Vector2(gui.ScreenRect.W - popupWidth - 12, gui.ScreenRect.H - popupHeight - Height - 12));
        }
    }

    static GuiColor StatusInk(BackgroundTaskStatus status) => status switch
    {
        BackgroundTaskStatus.Failed => Theme.Error,
        BackgroundTaskStatus.Cancelled => Theme.Warning,
        BackgroundTaskStatus.Completed => Theme.InkDim,
        _ => Theme.Ink,
    };

    static string Elapsed(BackgroundTask task) =>
        task.StartedAt is null
            ? string.Empty
            : task.Elapsed(DateTimeOffset.UtcNow).TotalSeconds.ToString("0.0s", CultureInfo.InvariantCulture);

    void SummaryRow(Gui gui, IReadOnlyList<BackgroundTask> snapshot)
    {
        var primary = BackgroundTaskTree.Primary(snapshot);
        var activeRoots = BackgroundTaskTree.ActiveRoots(snapshot);

        if (primary is null)
        {
            var finished = BackgroundTaskTree.Roots(snapshot).Count(t => t.IsFinished);
            Label(gui, finished == 1 ? "1 finished task" : $"{finished} finished tasks", Theme.InkDim);
        }
        else
        {
            var rollup = BackgroundTaskTree.Rollup(snapshot, primary);
            Label(gui, BackgroundTaskTree.Describe(primary, rollup), Theme.Ink);

            // Work that has not reported yet has nothing honest to show, so it animates rather than
            // sitting at an unmoving zero.
            var fraction = rollup.Progress > 0 ? rollup.Progress : (float?)null;
            gui.ProgressBar(fraction, barWidth, 5);

            if (activeRoots > 1) Label(gui, $"+{activeRoots - 1}", Theme.InkDim);
        }

        if (gui.GetInteractable().OnClick()) listOpen = !listOpen;
    }

    void TaskList(Gui gui)
    {
        var snapshot = tasks.Snapshot();
        var roots = BackgroundTaskTree.Roots(snapshot);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4).Padding(6).Enter())
        {
            ListHeader(gui, snapshot, roots);

            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(6).Enter())
            {
                gui.ScrollY();
                foreach (var root in roots) RootRow(gui, snapshot, root);
            }
        }
    }

    void ListHeader(Gui gui, IReadOnlyList<BackgroundTask> snapshot, IReadOnlyList<BackgroundTask> roots)
    {
        var finished = roots.Count(t => t.IsFinished);

        using (gui.Node(-1, 20).ExpandWidth().Direction(Axis.Horizontal).Gap(8).ContentAlignY(0.5f).Enter())
        {
            Label(gui, $"{BackgroundTaskTree.ActiveRoots(snapshot)} running, {finished} finished", Theme.InkDim, 11);

            // Completed tasks retire on their own; failures stay until read, and this dismisses them.
            if (finished > 0 && TextButton(gui, "Clear finished", "tasks/clear")) tasks.ClearFinished();
        }
    }

    void RootRow(Gui gui, IReadOnlyList<BackgroundTask> snapshot, BackgroundTask root)
    {
        var rollup = BackgroundTaskTree.Rollup(snapshot, root);

        using (gui.Node(-1, -1, $"tasks/{root.Id}").ExpandWidth().Direction(Axis.Vertical).Gap(3).Enter())
        {
            using (gui.Node(-1, 18).ExpandWidth().Direction(Axis.Horizontal).Gap(8).ContentAlignY(0.5f).Enter())
            {
                Label(gui, root.Label, StatusInk(rollup.Status));
                Label(gui, rollup.Status.Text(), Theme.InkDim, 11);
                Label(gui, Elapsed(root), Theme.InkDim, 11);

                if (root.IsActive && !root.CancelRequested
                                 && TextButton(gui, "Cancel", $"tasks/{root.Id}/cancel"))
                {
                    tasks.RequestCancel(root.Id);
                }
            }

            if (root.IsActive) gui.ProgressBar(rollup.Progress, -1, 5);
            if (root.Note.Length > 0) Label(gui, root.Note, StatusInk(rollup.Status), 11);

            foreach (var child in BackgroundTaskTree.ChildrenOf(snapshot, root.Id)) ChildRow(gui, child);
        }
    }

    static void ChildRow(Gui gui, BackgroundTask child)
    {
        using (gui.Node(-1, 16, $"tasks/child/{child.Id}")
                   .ExpandWidth().Direction(Axis.Horizontal).Gap(8).PaddingLeft(14).ContentAlignY(0.5f).Enter())
        {
            var detail = child.UnitsTotal > 0 ? $"{child.Label} ({child.UnitsDone}/{child.UnitsTotal})" : child.Label;
            Label(gui, detail, Theme.InkDim, 11);
            if (child.IsActive) gui.ProgressBar(child.Fraction, 70, 4);
        }
    }

    static void Label(Gui gui, string text, GuiColor color, float size = 12)
    {
        if (text.Length > 0) gui.DrawText(text, Theme.Text(size), color);
    }

    static bool TextButton(Gui gui, string text, string id)
    {
        using (gui.Node(-1, 16, id).Enter())
        {
            var interactable = gui.GetInteractable();
            gui.DrawText(text, Theme.Text(11), interactable.OnHover() ? Theme.Ink : Theme.InkDim);
            return interactable.OnClick();
        }
    }
}
