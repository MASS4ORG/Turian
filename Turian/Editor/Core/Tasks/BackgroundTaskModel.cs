namespace Turian.Editor.Core;

/// <summary>Lifecycle state of a background task.</summary>
public enum BackgroundTaskStatus
{
    /// <summary>Declared, but at least one dependency has not reached a terminal state.</summary>
    Blocked,

    /// <summary>Ready to run, waiting for a free worker slot.</summary>
    Queued,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished with an error; <see cref="BackgroundTask.Note"/> carries the message.</summary>
    Failed,

    /// <summary>Aborted in response to a cancel request.</summary>
    Cancelled,
}

/// <summary>Category of work, used for the icon and for picking the task the compact bar summarises.</summary>
public enum BackgroundTaskKind
{
    /// <summary>Uncategorised work.</summary>
    Generic,

    /// <summary>Walking the project tree for changed files.</summary>
    Scan,

    /// <summary>Turning source assets into engine artifacts.</summary>
    Import,

    /// <summary>Compiling user scripts.</summary>
    Compile,

    /// <summary>A full project build.</summary>
    Build,

    /// <summary>Producing a distributable package.</summary>
    Package,
}

/// <summary>
/// Editor capabilities a task holds while it is active. Actions are disabled per capability rather
/// than wholesale, so a long script compile still leaves scene editing and saving available.
/// </summary>
[Flags]
public enum EditorLocks : byte
{
    /// <summary>Holds nothing; every editor action stays available.</summary>
    None = 0,

    /// <summary>Asset artifacts and the asset database are being rewritten.</summary>
    Assets = 1 << 0,

    /// <summary>Compiled user-script binaries and reflection data are stale.</summary>
    Scripts = 1 << 1,

    /// <summary>Scene contents are being mutated from a worker.</summary>
    Scene = 1 << 2,

    /// <summary>Project-level files (build output, settings) are being written.</summary>
    Project = 1 << 3,
}

/// <summary>How a submission is resolved when an active task already has the same key.</summary>
public enum DuplicatePolicy
{
    /// <summary>Always create a new task, even alongside an identical one.</summary>
    Queue,

    /// <summary>
    /// Reuse the in-flight task and flag it to run once more when it lands, so a burst of clicks
    /// costs exactly one extra run.
    /// </summary>
    Coalesce,

    /// <summary>Ignore the submission and return the in-flight task's id.</summary>
    Drop,

    /// <summary>Cancel the in-flight task and queue a fresh one behind it.</summary>
    Restart,
}

/// <summary>Everything a submission may specify. Only <see cref="Label"/> is usually set.</summary>
[PublicAPI]
public sealed record BackgroundTaskSpec
{
    /// <summary>Human-readable name shown in the task list.</summary>
    public required string Label { get; init; }

    /// <summary>Category of work.</summary>
    public BackgroundTaskKind Kind { get; init; } = BackgroundTaskKind.Generic;

    /// <summary>Duplicate-detection key; defaults to <see cref="Label"/> when empty.</summary>
    public string? Key { get; init; }

    /// <summary>Capabilities the task holds while active.</summary>
    public EditorLocks Locks { get; init; } = EditorLocks.None;

    /// <summary>How to resolve a submission that collides with an active task of the same key.</summary>
    public DuplicatePolicy Policy { get; init; } = DuplicatePolicy.Queue;

    /// <summary>Ids that must reach a terminal state before this task may run.</summary>
    public IReadOnlyList<long> DependsOn { get; init; } = [];

    /// <summary>Start already running rather than queued.</summary>
    public bool StartImmediately { get; init; }

    /// <summary>Whether this task displays a modal overlay and blocks editor input while active.</summary>
    public bool BlocksUi { get; init; }
}

/// <summary>
/// Immutable view of one task, as handed to the UI. Snapshots are taken under the manager's lock so
/// a frame always renders a consistent set.
/// </summary>
[PublicAPI]
public sealed record BackgroundTask
{
    /// <summary>Unique, monotonically increasing id. Never zero.</summary>
    public required long Id { get; init; }

    /// <summary>Owning task, or zero for a root. Children roll up into the parent's aggregate.</summary>
    public long ParentId { get; init; }

    /// <summary>Category of work.</summary>
    public BackgroundTaskKind Kind { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public BackgroundTaskStatus Status { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Label { get; init; }

    /// <summary>Most recent status detail, or the failure message once failed.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Duplicate-detection key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Own completion fraction in 0..1; ignored once <see cref="UnitsTotal"/> is set.</summary>
    public float Progress { get; init; }

    /// <summary>
    /// Share of the parent's aggregate this child accounts for. Phases of wildly different cost
    /// would otherwise make the parent bar lie.
    /// </summary>
    public float Weight { get; init; } = 1;

    /// <summary>Items processed so far, for batch work.</summary>
    public long UnitsDone { get; init; }

    /// <summary>Total items to process; zero means the task reports a fraction instead.</summary>
    public long UnitsTotal { get; init; }

    /// <summary>
    /// Total child weight the owner intends to open, declared up front. Without it the aggregate
    /// divides by the children opened so far, so opening a heavy phase drags the parent backwards.
    /// </summary>
    public float PlannedWeight { get; init; }

    /// <summary>Whether cooperative cancellation has been requested.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>Set when a <see cref="DuplicatePolicy.Coalesce"/> duplicate arrived mid-flight.</summary>
    public bool RerunRequested { get; init; }

    /// <summary>Capabilities held while this task is active.</summary>
    public EditorLocks Locks { get; init; }

    /// <summary>Whether this task blocks editor input while active.</summary>
    public bool BlocksUi { get; init; }

    /// <summary>Ids that must land before this task may run.</summary>
    public IReadOnlyList<long> DependsOn { get; init; } = [];

    /// <summary>When the task started running, or null while queued or blocked.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the task reached a terminal state, or null while active.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>True once the task can no longer change state.</summary>
    public bool IsFinished => Status is BackgroundTaskStatus.Completed
        or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Cancelled;

    /// <summary>True while the task may still change state.</summary>
    public bool IsActive => !IsFinished;

    /// <summary>Completion fraction in 0..1, from item counters when available.</summary>
    public float Fraction => UnitsTotal == 0
        ? Progress
        : Math.Min(UnitsDone, UnitsTotal) / (float)UnitsTotal;

    /// <summary>Time spent so far, or in total once finished. Zero until the task starts.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        StartedAt is not { } start ? TimeSpan.Zero : (FinishedAt ?? now) - start;
}

/// <summary>Display helpers shared by every surface that renders tasks.</summary>
public static class BackgroundTaskText
{
    /// <summary>The word shown for a status.</summary>
    public static string Text(this BackgroundTaskStatus status) => status switch
    {
        BackgroundTaskStatus.Blocked => "Waiting",
        BackgroundTaskStatus.Queued => "Queued",
        BackgroundTaskStatus.Running => "Running",
        BackgroundTaskStatus.Completed => "Completed",
        BackgroundTaskStatus.Failed => "Failed",
        BackgroundTaskStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    /// <summary>The word shown for a kind.</summary>
    public static string Text(this BackgroundTaskKind kind) => kind switch
    {
        BackgroundTaskKind.Generic => "Task",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Ranking used to pick which of several parallel tasks the compact task bar summarises — the
    /// most disruptive operation wins the one visible row.
    /// </summary>
    public static int Priority(this BackgroundTaskKind kind) => kind switch
    {
        BackgroundTaskKind.Build => 5,
        BackgroundTaskKind.Package => 4,
        BackgroundTaskKind.Compile => 3,
        BackgroundTaskKind.Import => 2,
        BackgroundTaskKind.Scan => 1,
        _ => 0,
    };
}
