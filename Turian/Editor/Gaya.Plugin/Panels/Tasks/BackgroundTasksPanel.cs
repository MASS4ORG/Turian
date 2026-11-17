namespace Gaya.Plugin.Turian;

/// <summary>
/// The background tasks as a regular dockable panel: every running and finished operation with its status, elapsed
/// time, progress bar and nested phases, a Cancel button per running task and a button that clears the finished ones.
/// The status bar shows a one-line summary and opens this panel.
/// </summary>
sealed class BackgroundTasksPanel(BackgroundTaskManager tasks) : IPanel
{
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

        var roots = BackgroundTaskTree.Roots(snapshot);
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4).Padding(6).Enter())
        {
            ListHeader(gui, snapshot, roots);

            using (gui.Node(-1, -1, "tasks/list").Expand().Direction(Axis.Vertical).Gap(6).Enter())
            {
                gui.ScrollY();

                foreach (var root in roots)
                {
                    RootRow(gui, snapshot, root);
                }
            }
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

    void ListHeader(Gui gui, IReadOnlyList<BackgroundTask> snapshot, IReadOnlyList<BackgroundTask> roots)
    {
        var finished = roots.Count(t => t.IsFinished);

        using (gui.Node(-1, 20).ExpandWidth().Direction(Axis.Horizontal).Gap(8).ContentAlignY(0.5f).Enter())
        {
            Label(gui, $"{BackgroundTaskTree.ActiveRoots(snapshot)} running, {finished} finished", Theme.InkDim, 11);

            // Completed tasks retire on their own; failures stay until read, and this dismisses them.
            if (finished > 0 && TextButton(gui, "Clear finished", "tasks/clear"))
            {
                tasks.ClearFinished();
            }
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

                if (root is { IsActive: true, CancelRequested: false }
                    && TextButton(gui, "Cancel", $"tasks/{root.Id}/cancel"))
                {
                    tasks.RequestCancel(root.Id);
                }
            }

            if (root.IsActive)
            {
                gui.ProgressBar(rollup.Progress, -1, 5);
            }

            if (root.Note.Length > 0)
            {
                Label(gui, root.Note, StatusInk(rollup.Status), 11);
            }

            foreach (var child in BackgroundTaskTree.ChildrenOf(snapshot, root.Id))
            {
                ChildRow(gui, child);
            }
        }
    }

    static void ChildRow(Gui gui, BackgroundTask child)
    {
        using (gui.Node(-1, 16, $"tasks/child/{child.Id}")
                   .ExpandWidth().Direction(Axis.Horizontal).Gap(8).PaddingLeft(14).ContentAlignY(0.5f).Enter())
        {
            var detail = child.UnitsTotal > 0 ? $"{child.Label} ({child.UnitsDone}/{child.UnitsTotal})" : child.Label;
            Label(gui, detail, Theme.InkDim, 11);
            if (child.IsActive)
            {
                gui.ProgressBar(child.Fraction, 70, 4);
            }
        }
    }

    static void Label(Gui gui, string text, GuiColor color, float size = 12)
    {
        if (text.Length > 0)
        {
            gui.DrawText(text, Theme.Text(size), color);
        }
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
