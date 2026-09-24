
namespace Turian.Editor.Core;

/// <summary>
/// Registry of long-running editor operations. Workers report into it from any thread; the UI takes
/// a <see cref="Snapshot"/> once per frame and renders without holding the lock. It tracks the work
/// rather than running it — see <see cref="BackgroundTaskRunner"/> for execution.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed partial class BackgroundTaskManager
{
    readonly Lock gate = new();
    readonly List<Entry> tasks = [];
    readonly TimeProvider clock;
    long nextId = 1;

    /// <summary>Creates a manager driven by the system clock.</summary>
    public BackgroundTaskManager() : this(TimeProvider.System)
    {
    }

    /// <summary>Creates a manager driven by the given clock, so tests can advance time.</summary>
    /// <param name="clock">Clock used to stamp start and finish times.</param>
    public BackgroundTaskManager(TimeProvider clock) => this.clock = clock;

    /// <summary>
    /// Raised whenever a task is created or changes state, so a shell can repaint. Handlers run off
    /// the manager's lock and may arrive on a worker thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// How long a completed root lingers before <see cref="Tick"/> reclaims it. Failed and cancelled
    /// tasks are sticky — they carry a diagnosis nobody has read yet. Zero keeps everything until
    /// <see cref="ClearFinished"/>.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Number of tasks that have not reached a terminal state.</summary>
    public int ActiveCount
    {
        get
        {
            lock (gate) return tasks.Count(t => !IsTerminal(t.Status));
        }
    }

    /// <summary>Total number of tracked tasks, active and finished.</summary>
    public int TotalCount
    {
        get
        {
            lock (gate) return tasks.Count;
        }
    }

    /// <summary>
    /// Union of the capabilities held by every active task. Gate individual UI actions on this
    /// instead of blocking the whole editor.
    /// </summary>
    public EditorLocks ActiveLocks
    {
        get
        {
            lock (gate)
            {
                var locks = EditorLocks.None;
                foreach (var t in tasks)
                    if (!IsTerminal(t.Status))
                        locks |= t.Locks;
                return locks;
            }

        }
    }

    /// <summary>Whether any active task requested a whole-editor input block.</summary>
    public bool IsUiBlocked
    {
        get
        {
            lock (gate) return tasks.Any(t => !IsTerminal(t.Status) && t.BlocksUi);
        }
    }

    /// <summary>The active task responsible for the whole-editor input block, if any.</summary>
    public BackgroundTask? BlockingTask
    {
        get
        {
            lock (gate) return tasks.FirstOrDefault(t => !IsTerminal(t.Status) && t.BlocksUi)?.ToSnapshot();
        }
    }

    /// <summary>
    /// Creates a task, applying <see cref="BackgroundTaskSpec.Policy"/> against any active task with
    /// the same key. Returns the new id, or the reused id for
    /// <see cref="DuplicatePolicy.Coalesce"/> and <see cref="DuplicatePolicy.Drop"/>.
    /// </summary>
    /// <param name="spec">What to create.</param>
    /// <returns>The id to report progress against.</returns>
    public long Submit(BackgroundTaskSpec spec) => Submit(spec, out _);

    /// <summary>
    /// Creates a task as <see cref="Submit(BackgroundTaskSpec)"/> does, also reporting whether a new
    /// task was created — a caller that launches the work must not launch it twice when a
    /// submission coalesced onto an in-flight task.
    /// </summary>
    /// <param name="spec">What to create.</param>
    /// <param name="created">False when an existing task was reused instead.</param>
    /// <returns>The id to report progress against.</returns>
    public long Submit(BackgroundTaskSpec spec, out bool created)
    {
        ArgumentNullException.ThrowIfNull(spec);
        created = false;
        long id;
        lock (gate)
        {
            var key = string.IsNullOrEmpty(spec.Key) ? spec.Label : spec.Key;
            if (spec.Policy != DuplicatePolicy.Queue && FindActiveByKey(key) is { } existing)
            {
                switch (spec.Policy)
                {
                    case DuplicatePolicy.Coalesce:
                        existing.RerunRequested = true;
                        return existing.Id;
                    case DuplicatePolicy.Drop:
                        return existing.Id;
                    case DuplicatePolicy.Queue:
                        break;
                    case DuplicatePolicy.Restart:
                        existing.CancelRequested = true;
                        break;
                }
            }

            id = nextId++;
            tasks.Add(new Entry
            {
                Id = id,
                Kind = spec.Kind,
                Label = spec.Label,
                Key = key,
                Locks = spec.Locks,
                BlocksUi = spec.BlocksUi,
                DependsOn = [.. spec.DependsOn],
                Status = spec.DependsOn.Count > 0
                    ? BackgroundTaskStatus.Blocked
                    : spec.StartImmediately ? BackgroundTaskStatus.Running : BackgroundTaskStatus.Queued,
                StartedAt = spec.StartImmediately ? clock.GetUtcNow() : null,
            });
        }

        created = true;
        Changed?.Invoke();
        return id;
    }

    /// <summary>Creates a task already in the running state.</summary>
    /// <param name="kind">Category of work.</param>
    /// <param name="label">Name shown in the task list.</param>
    /// <returns>The new task id.</returns>
    public long Begin(BackgroundTaskKind kind, string label) =>
        Submit(new BackgroundTaskSpec { Kind = kind, Label = label, StartImmediately = true });

    /// <summary>Creates a task in the queued state.</summary>
    /// <param name="kind">Category of work.</param>
    /// <param name="label">Name shown in the task list.</param>
    /// <returns>The new task id.</returns>
    public long Enqueue(BackgroundTaskKind kind, string label) =>
        Submit(new BackgroundTaskSpec { Kind = kind, Label = label });

    /// <summary>Creates a running child of <paramref name="parentId"/>.</summary>
    /// <param name="parentId">The owning task.</param>
    /// <param name="kind">Category of work.</param>
    /// <param name="label">Name shown while the phase runs.</param>
    /// <param name="weight">Its share of the parent's aggregate.</param>
    /// <returns>The new task id.</returns>
    public long BeginChild(long parentId, BackgroundTaskKind kind, string label, float weight = 1)
    {
        long id;
        lock (gate)
        {
            id = nextId++;
            tasks.Add(new Entry
            {
                Id = id,
                ParentId = parentId,
                Kind = kind,
                Label = label,
                Key = label,
                Weight = weight,
                Status = BackgroundTaskStatus.Running,
                StartedAt = clock.GetUtcNow(),
            });
        }

        Changed?.Invoke();
        return id;
    }

    /// <summary>Moves a queued task to running. No-op for unknown or already-running tasks.</summary>
    /// <param name="id">The task to start.</param>
    public void Start(long id) => Mutate(id, t =>
    {
        if (t.Status == BackgroundTaskStatus.Queued) t.Status = BackgroundTaskStatus.Running;
    });

    /// <summary>
    /// Updates a task's progress fraction and, when non-empty, its note. A queued task transitions
    /// to running on its first progress update.
    /// </summary>
    /// <param name="id">The task to update.</param>
    /// <param name="fraction">Completion in 0..1; clamped.</param>
    /// <param name="note">Short detail line, or empty to leave it unchanged.</param>
    public void SetProgress(long id, float fraction, string note = "") => Mutate(id, t =>
    {
        if (t.Status == BackgroundTaskStatus.Queued) t.Status = BackgroundTaskStatus.Running;
        t.Progress = Math.Clamp(fraction, 0, 1);
        if (!string.IsNullOrEmpty(note)) t.Note = note;
    });

    /// <summary>
    /// Publishes item counters for batch work. Once set they drive the task's fraction, so a
    /// 1000-asset import reports honest progress without spawning 1000 child tasks.
    /// </summary>
    /// <param name="id">The task to update.</param>
    /// <param name="done">Items processed so far.</param>
    /// <param name="total">Items in the batch.</param>
    public void SetUnits(long id, long done, long total) => Mutate(id, t =>
    {
        if (t.Status == BackgroundTaskStatus.Queued) t.Status = BackgroundTaskStatus.Running;
        t.UnitsDone = Math.Max(0, done);
        t.UnitsTotal = Math.Max(0, total);
    });

    /// <summary>
    /// Declares the total child weight this task will open, so its aggregate has a stable
    /// denominator from the start rather than one that grows per phase.
    /// </summary>
    /// <param name="id">The task to update.</param>
    /// <param name="totalWeight">Combined weight of the phases to come.</param>
    public void SetPlannedWeight(long id, float totalWeight) =>
        Mutate(id, t => t.PlannedWeight = Math.Max(0, totalWeight));

    /// <summary>
    /// Requests cooperative cancellation of a task and everything under it. The running operation
    /// observes this through its progress sink and should abort, then finalise with
    /// <see cref="Cancel"/>.
    /// </summary>
    /// <param name="id">The task to cancel.</param>
    public void RequestCancel(long id)
    {
        lock (gate)
        {
            foreach (var t in tasks)
                if (t.Id == id || IsDescendantOf(t, id))
                    t.CancelRequested = true;
        }

        Changed?.Invoke();
    }

    /// <summary>Whether cancellation has been requested for the given task.</summary>
    /// <param name="id">The task to query.</param>
    /// <returns>True when the operation should abort.</returns>
    public bool IsCancelRequested(long id)
    {
        lock (gate) return Find(id)?.CancelRequested ?? false;
    }

    /// <summary>
    /// Consumes a pending rerun flag set by a coalesced duplicate. Owners call this when a task
    /// lands and relaunch once if it returns true.
    /// </summary>
    /// <param name="id">The task that just landed.</param>
    /// <returns>True when the operation should run once more.</returns>
    public bool TakeRerunRequest(long id)
    {
        lock (gate)
        {
            if (Find(id) is not { } t) return false;
            var pending = t.RerunRequested;
            t.RerunRequested = false;
            return pending;
        }
    }

    /// <summary>Marks a task completed; progress is forced to full.</summary>
    /// <param name="id">The task that finished.</param>
    public void Complete(long id) => Finish(id, BackgroundTaskStatus.Completed, "");

    /// <summary>Marks a task failed with an explanatory message.</summary>
    /// <param name="id">The task that failed.</param>
    /// <param name="message">Why it failed, shown in the task list.</param>
    public void Fail(long id, string message) => Finish(id, BackgroundTaskStatus.Failed, message);

    /// <summary>Marks a task cancelled. Use after observing a cancel request.</summary>
    /// <param name="id">The task that aborted.</param>
    public void Cancel(long id) => Finish(id, BackgroundTaskStatus.Cancelled, "");

    /// <summary>Snapshots a single task, or null when it is unknown or already reclaimed.</summary>
    /// <param name="id">The task to read.</param>
    /// <returns>An immutable view, or null.</returns>
    public BackgroundTask? Get(long id)
    {
        lock (gate) return Find(id)?.ToSnapshot();
    }

    /// <summary>
    /// Immutable view of every tracked task in submission order. Take one per frame and render from
    /// it, so a frame never mixes states from different moments.
    /// </summary>
    /// <returns>All tasks, active and finished.</returns>
    public IReadOnlyList<BackgroundTask> Snapshot()
    {
        lock (gate) return [.. tasks.Select(t => t.ToSnapshot())];
    }

    /// <summary>The active task holding any of <paramref name="wanted"/>, for naming the blocker.</summary>
    /// <param name="wanted">Capabilities the caller needs.</param>
    /// <returns>The holding task, or null when nothing holds them.</returns>
    public BackgroundTask? LockOwner(EditorLocks wanted)
    {
        lock (gate)
            return tasks.FirstOrDefault(t => !IsTerminal(t.Status) && (t.Locks & wanted) != 0)?.ToSnapshot();
    }

    /// <summary>
    /// Id of the next task ready to run — queued, in submission order. Blocked tasks become queued
    /// once <see cref="Tick"/> resolves their dependencies.
    /// </summary>
    /// <returns>The id, or null when nothing is ready.</returns>
    public long? NextReady()
    {
        lock (gate) return tasks.FirstOrDefault(t => t.Status == BackgroundTaskStatus.Queued)?.Id;
    }

    /// <summary>
    /// Advances bookkeeping that needs a clock: stamps start and finish times, releases
    /// dependency-blocked tasks, cascades dependency failures, and reclaims completed roots past
    /// their retention. Call once per frame.
    /// </summary>
    public void Tick()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            foreach (var t in tasks)
            {
                if (t.Status == BackgroundTaskStatus.Running && t.StartedAt is null) t.StartedAt = now;
                if (IsTerminal(t.Status) && t.FinishedAt is null) t.FinishedAt = now;
            }

            ResolveDependenciesLocked();
            if (Retention > TimeSpan.Zero) ReclaimLocked(now);
        }
    }

    /// <summary>
    /// Releases dependency-blocked tasks whose dependencies landed and cancels those whose
    /// dependencies broke, without the rest of <see cref="Tick"/>'s bookkeeping. For synchronous
    /// drains that have no frame clock to advance.
    /// </summary>
    public void ResolveDependencies()
    {
        lock (gate) ResolveDependenciesLocked();
        Changed?.Invoke();
    }

    /// <summary>Removes every task that has reached a terminal state.</summary>
    public void ClearFinished()
    {
        lock (gate) tasks.RemoveAll(t => IsTerminal(t.Status));
        Changed?.Invoke();
    }

    /// <summary>
    /// A progress sink bound to the given task. Pass it to an operation so its reports, sub-phases
    /// and cancellation polls flow into this manager.
    /// </summary>
    /// <param name="id">The task to report against.</param>
    /// <returns>A sink writing into this manager.</returns>
    public IProgressScope ProgressFor(long id) => new Sink(this, id);

    static bool IsTerminal(BackgroundTaskStatus status) => status
        is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Cancelled;

    Entry? Find(long id) => tasks.FirstOrDefault(t => t.Id == id);

    Entry? FindActiveByKey(string key) =>
        tasks.FirstOrDefault(t => t.ParentId == 0 && !IsTerminal(t.Status) && t.Key == key);

    bool IsDescendantOf(Entry task, long ancestorId)
    {
        for (var parent = task.ParentId; parent != 0;)
        {
            if (parent == ancestorId) return true;
            parent = Find(parent)?.ParentId ?? 0;
        }

        return false;
    }

    void Mutate(long id, Action<Entry> change)
    {
        lock (gate)
        {
            if (Find(id) is not { } t) return;
            change(t);
        }

        Changed?.Invoke();
    }

    void Finish(long id, BackgroundTaskStatus status, string note) => Mutate(id, t =>
    {
        t.Status = status;
        t.FinishedAt = clock.GetUtcNow();
        if (status == BackgroundTaskStatus.Completed)
        {
            t.Progress = 1;
            t.UnitsDone = t.UnitsTotal;
        }

        if (!string.IsNullOrEmpty(note)) t.Note = note;
    });

    void ResolveDependenciesLocked()
    {
        foreach (var t in tasks)
        {
            if (t.Status != BackgroundTaskStatus.Blocked) continue;

            var allDone = true;
            var anyBroken = false;
            foreach (var depId in t.DependsOn)
            {
                if (Find(depId) is not { } dep) continue;
                switch (dep.Status)
                {
                    case BackgroundTaskStatus.Completed: break;
                    case BackgroundTaskStatus.Failed:
                    case BackgroundTaskStatus.Cancelled:
                        anyBroken = true;
                        break;
                    default:
                        allDone = false;
                        break;
                }
            }

            if (anyBroken)
            {
                t.Status = BackgroundTaskStatus.Cancelled;
                t.Note = "Dependency did not finish";
            }
            else if (allDone)
            {
                t.Status = BackgroundTaskStatus.Queued;
            }
        }
    }

    void ReclaimLocked(DateTimeOffset now)
    {
        tasks.RemoveAll(t => t.ParentId == 0
                              && t.Status == BackgroundTaskStatus.Completed
                              && now - (t.FinishedAt ?? now) >= Retention);
        tasks.RemoveAll(t => t.ParentId != 0 && Find(t.ParentId) is null);
    }

}
