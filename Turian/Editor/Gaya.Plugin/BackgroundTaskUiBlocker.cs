namespace Gaya.Plugin.Turian;

sealed class BackgroundTaskUiBlocker(BackgroundTaskManager tasks) : IUiBlocker
{
    /// <inheritdoc />
    public bool IsBlocked => tasks.IsUiBlocked;

    /// <inheritdoc />
    public string Message => tasks.BlockingTask?.Label ?? "Working...";

    /// <inheritdoc />
    public UiBlockerProgress? Progress
    {
        get
        {
            var snapshot = tasks.Snapshot();
            var root = snapshot.FirstOrDefault(task => task is { IsActive: true, BlocksUi: true });
            if (root is null) return null;
            var phase = ActivePhase(snapshot, root);
            return new UiBlockerProgress(root.Label, Detail(root, phase), Fraction(phase), Counter(phase),
                root.Elapsed(DateTimeOffset.UtcNow));
        }
    }

    static string Detail(BackgroundTask root, BackgroundTask phase)
    {
        if (phase.Id == root.Id) return phase.Note;
        return phase.Note.Length > 0 ? $"{phase.Label}: {phase.Note}" : phase.Label;
    }

    static float? Fraction(BackgroundTask phase) =>
        phase.UnitsTotal > 0 || phase.Progress > 0 ? phase.Fraction : null;

    static string Counter(BackgroundTask phase) =>
        phase.UnitsTotal > 0 ? $"{phase.UnitsDone:N0} / {phase.UnitsTotal:N0}" : "";

    static BackgroundTask ActivePhase(IReadOnlyList<BackgroundTask> tasks, BackgroundTask parent)
    {
        while (tasks.FirstOrDefault(task => task.ParentId == parent.Id && task.IsActive) is { } child)
            parent = child;
        return parent;
    }
}
