namespace Turian.Editor.Core;

/// <summary>Aggregate state of one root task and everything beneath it.</summary>
[PublicAPI]
public readonly record struct TaskRollup
{
    /// <summary>
    /// Completion in 0..1: the weighted mean of child progress when the task has children,
    /// otherwise the task's own fraction.
    /// </summary>
    public float Progress { get; init; }

    /// <summary>
    /// Effective status — a parent reads as running while any child is, and inherits the worst
    /// terminal outcome of its children otherwise.
    /// </summary>
    public BackgroundTaskStatus Status { get; init; }

    /// <summary>How many direct children the root has.</summary>
    public int ChildCount { get; init; }

    /// <summary>How many of them reached a terminal state.</summary>
    public int ChildrenDone { get; init; }

    /// <summary>The running child, whose label is the most specific thing to show.</summary>
    public BackgroundTask? ActiveChild { get; init; }
}

/// <summary>
/// Read-side view over a <see cref="BackgroundTaskManager"/> snapshot: separates roots from their
/// children, rolls child progress up into an aggregate, and formats the one line a compact display
/// can afford.
/// </summary>
public static class BackgroundTaskTree
{
    /// <summary>Rolls <paramref name="root"/> up over its children in <paramref name="tasks"/>.</summary>
    /// <param name="tasks">A manager snapshot.</param>
    /// <param name="root">The task to aggregate.</param>
    /// <returns>The aggregate a display should show for that root.</returns>
    public static TaskRollup Rollup(IReadOnlyList<BackgroundTask> tasks, BackgroundTask root)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(root);

        var childCount = 0;
        var childrenDone = 0;
        var weightTotal = 0f;
        var weighted = 0f;
        var anyActive = false;
        var anyFailed = false;
        var anyCancelled = false;
        BackgroundTask? activeChild = null;

        foreach (var c in tasks)
        {
            if (c.ParentId != root.Id) continue;
            childCount++;
            var w = c.Weight > 0 ? c.Weight : 1;
            weightTotal += w;
            weighted += w * c.Fraction;
            switch (c.Status)
            {
                case BackgroundTaskStatus.Completed:
                    childrenDone++;
                    break;
                case BackgroundTaskStatus.Failed:
                    anyFailed = true;
                    childrenDone++;
                    break;
                case BackgroundTaskStatus.Cancelled:
                    anyCancelled = true;
                    childrenDone++;
                    break;
                case BackgroundTaskStatus.Running:
                    anyActive = true;
                    activeChild ??= c;
                    break;
                default:
                    anyActive = true;
                    break;
            }
        }

        if (childCount == 0)
            return new TaskRollup { Progress = root.Fraction, Status = root.Status };

        // Divide by the planned total when the owner declared one, so the bar does not lurch
        // backwards each time another phase opens.
        var denominator = Math.Max(weightTotal, root.PlannedWeight);
        var progress = denominator > 0 ? Math.Min(weighted / denominator, 1) : 0;

        var rollup = new TaskRollup
        {
            Progress = progress,
            ChildCount = childCount,
            ChildrenDone = childrenDone,
            ActiveChild = activeChild,
        };

        // A finished root is authoritative: it knows outcomes its children cannot see.
        if (root.IsFinished)
            return rollup with
            {
                Status = root.Status,
                Progress = root.Status == BackgroundTaskStatus.Completed ? 1 : progress,
            };

        return rollup with
        {
            Status = anyActive ? BackgroundTaskStatus.Running
                : anyFailed ? BackgroundTaskStatus.Failed
                : anyCancelled ? BackgroundTaskStatus.Cancelled
                : BackgroundTaskStatus.Completed,
        };
    }

    /// <summary>The tasks without a parent, in submission order.</summary>
    /// <param name="tasks">A manager snapshot.</param>
    /// <returns>The root tasks.</returns>
    public static IReadOnlyList<BackgroundTask> Roots(IReadOnlyList<BackgroundTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return [.. tasks.Where(t => t.ParentId == 0)];
    }

    /// <summary>The direct children of <paramref name="parentId"/>, in submission order.</summary>
    /// <param name="tasks">A manager snapshot.</param>
    /// <param name="parentId">The owning task.</param>
    /// <returns>Its direct children.</returns>
    public static IReadOnlyList<BackgroundTask> ChildrenOf(IReadOnlyList<BackgroundTask> tasks, long parentId)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return [.. tasks.Where(t => t.ParentId == parentId)];
    }

    /// <summary>
    /// The active root a single-line display should summarise: the most disruptive kind, and among
    /// equals the one submitted first, since it will finish first.
    /// </summary>
    /// <param name="tasks">A manager snapshot.</param>
    /// <returns>The task to show, or null when nothing is active.</returns>
    public static BackgroundTask? Primary(IReadOnlyList<BackgroundTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        BackgroundTask? best = null;
        foreach (var t in tasks)
        {
            if (t.ParentId != 0 || !t.IsActive) continue;
            if (best is null || t.Kind.Priority() > best.Kind.Priority()) best = t;
        }

        return best;
    }

    /// <summary>How many roots are active — how many things are running in parallel.</summary>
    /// <param name="tasks">A manager snapshot.</param>
    /// <returns>The count of active roots.</returns>
    public static int ActiveRoots(IReadOnlyList<BackgroundTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return tasks.Count(t => t.ParentId == 0 && t.IsActive);
    }

    /// <summary>
    /// One-line description of a root: its label plus the most specific detail available — the
    /// running child's label, an item count, or the raw note.
    /// </summary>
    /// <param name="task">The root to describe.</param>
    /// <param name="rollup">Its aggregate, from <see cref="Rollup"/>.</param>
    /// <returns>The line to show in a compact display.</returns>
    public static string Describe(BackgroundTask task, TaskRollup rollup)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (rollup.ActiveChild is { } child)
            return child.UnitsTotal > 0
                ? $"{task.Label} — {child.Label} ({child.UnitsDone}/{child.UnitsTotal})"
                : $"{task.Label} — {child.Label}";

        if (task.UnitsTotal > 0) return $"{task.Label} ({task.UnitsDone}/{task.UnitsTotal})";
        return task.Note.Length > 0 ? $"{task.Label}: {task.Note}" : task.Label;
    }
}
