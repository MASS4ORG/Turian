namespace Turian.Editor.Core;

public sealed partial class BackgroundTaskManager
{
    sealed class Entry
    {
        public required long Id { get; init; }
        public long ParentId { get; init; }
        public BackgroundTaskKind Kind { get; init; }
        public required string Label { get; init; }
        public required string Key { get; init; }
        public EditorLocks Locks { get; init; }
        public bool BlocksUi { get; init; }
        public float Weight { get; init; } = 1;
        public IReadOnlyList<long> DependsOn { get; init; } = [];
        public BackgroundTaskStatus Status { get; set; }
        public string Note { get; set; } = string.Empty;
        public float Progress { get; set; }
        public long UnitsDone { get; set; }
        public long UnitsTotal { get; set; }
        public float PlannedWeight { get; set; }
        public bool CancelRequested { get; set; }
        public bool RerunRequested { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }

        public BackgroundTask ToSnapshot() => new()
        {
            Id = Id,
            ParentId = ParentId,
            Kind = Kind,
            Status = Status,
            Label = Label,
            Note = Note,
            Key = Key,
            Progress = Progress,
            Weight = Weight,
            UnitsDone = UnitsDone,
            UnitsTotal = UnitsTotal,
            PlannedWeight = PlannedWeight,
            CancelRequested = CancelRequested,
            RerunRequested = RerunRequested,
            Locks = Locks,
            BlocksUi = BlocksUi,
            DependsOn = DependsOn,
            StartedAt = StartedAt,
            FinishedAt = FinishedAt,
        };
    }

    /// <summary>
    /// Progress sink implementation keeps operation-facing reporting separate from the task registry.
    /// It remains bound to the manager and task id, preserving the same cancellation and completion
    /// semantics as the manager's public methods.
    /// </summary>
    sealed class Sink(BackgroundTaskManager manager, long id) : IProgressScope
    {
        bool finished;

        public bool IsCancelled => manager.IsCancelRequested(id);

        public void Report(float fraction, string note = "") => manager.SetProgress(id, fraction, note);

        public void Units(long done, long total) => manager.SetUnits(id, done, total);

        public void PlanChildren(float totalWeight) => manager.SetPlannedWeight(id, totalWeight);

        public IProgressScope BeginChild(BackgroundTaskKind kind, string label, float weight = 1) =>
            new Sink(manager, manager.BeginChild(id, kind, label, weight));

        public void Finish(bool ok)
        {
            if (finished) return;
            finished = true;
            if (ok) manager.Complete(id);
            else manager.Fail(id, "");
        }

        public void Dispose()
        {
            if (finished) return;
            finished = true;
            if (IsCancelled) manager.Cancel(id);
            else manager.Complete(id);
        }
    }
}
