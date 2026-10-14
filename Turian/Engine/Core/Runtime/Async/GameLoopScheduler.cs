namespace Turian.Engine.Core;

/// <summary>Owns gameplay waits and dispatches their continuations on one session's game thread.</summary>
public sealed partial class GameLoopScheduler : IDisposable
{
    readonly int threadId = Environment.CurrentManagedThreadId;
    readonly object gate = new();
    readonly Stack<GameLoopOperation> pool = new();
    readonly HashSet<GameLoopOperation> outstanding = [];
    readonly Queue<OperationLease> updates = new();
    readonly Queue<OperationLease> fixedUpdates = new();
    readonly Queue<OperationLease> lateUpdates = new();
    readonly ConcurrentQueue<ReadyWork> ready = new();
    readonly List<OperationLease> cancellationSnapshot = [];
    readonly CancellationTokenSource lifetime = new();
    readonly CancellationToken sessionToken;
    readonly TimeProvider timeProvider;
    readonly long origin;
    readonly GameLoopContext context;
    long generation;
    bool draining;
    bool shuttingDown;
    volatile bool stopped;

    /// <summary>Creates a session on the calling thread, optionally using a synthetic monotonic clock.</summary>
    public GameLoopScheduler(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        origin = this.timeProvider.GetTimestamp();
        context = new GameLoopContext(this);
        sessionToken = lifetime.Token;
#if DEBUG
        CaptureCreationStacks = true;
#endif
    }

    /// <summary>Captures scheduling stacks for outstanding-operation diagnostics; enabled by default in Debug.</summary>
    public bool CaptureCreationStacks { get; init; }

    /// <summary>Whether shutdown has started; a stopped scheduler cannot be restarted.</summary>
    public bool IsStopped => stopped;

    /// <summary>Whether the caller is the session's game thread.</summary>
    public bool IsMainThread => Environment.CurrentManagedThreadId == threadId;

    /// <summary>Cancels when this session stops.</summary>
    public CancellationToken SessionCancellationToken => sessionToken;

    /// <summary>The current presentation frame, incremented independently of fixed ticks.</summary>
    public long FrameIndex { get; private set; }

    /// <summary>Scaled presentation time, advanced once per frame.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>Presentation time unaffected by time scale and advanced only by pumped frames.</summary>
    public double UnscaledElapsedSeconds { get; private set; }

    /// <summary>Successfully completed fixed-tick time.</summary>
    public double FixedElapsedSeconds { get; private set; }

    /// <summary>The number of successfully completed fixed steps observed by the scheduler.</summary>
    public long FixedIndex { get; private set; }

    /// <summary>The number of registered operations, including completed values awaiting consumption.</summary>
    public int OutstandingCount
    {
        get { lock (gate) return outstanding.Count; }
    }

    /// <summary>Installs this session's synchronization context until the returned scope is disposed.</summary>
    public ContextScope EnterContext()
    {
        RequireMainThread();
        return new ContextScope(context);
    }

    /// <summary>Advances presentation clocks; hosts call this once before scene callbacks and input processing.</summary>
    public void BeginFrame(double scaledDeltaTime, double unscaledDeltaTime)
    {
        RequireIdle();
        ObjectDisposedException.ThrowIf(stopped, this);
        ValidateDuration(scaledDeltaTime);
        ValidateDuration(unscaledDeltaTime);
        ValidateDuration(ElapsedSeconds + scaledDeltaTime);
        ValidateDuration(UnscaledElapsedSeconds + unscaledDeltaTime);
        FrameIndex++;
        ElapsedSeconds += scaledDeltaTime;
        UnscaledElapsedSeconds += unscaledDeltaTime;
        PumpCancellation();
    }

    /// <summary>Releases fixed waits after the simulation has successfully completed and published a tick.</summary>
    public void CompleteFixedStep(double fixedDeltaTime)
    {
        RequireIdle();
        ObjectDisposedException.ThrowIf(stopped, this);
        ValidateDuration(fixedDeltaTime);
        ValidateDuration(FixedElapsedSeconds + fixedDeltaTime);
        FixedIndex++;
        FixedElapsedSeconds += fixedDeltaTime;
        Drain(fixedUpdates, false);
    }

    /// <summary>Runs a snapshot of posted work followed by eligible waits after all component updates.</summary>
    public void DrainUpdate() => Drain(updates, true);

    /// <summary>Runs eligible late-update waits after all component late updates.</summary>
    public void DrainLateUpdate() => Drain(lateUpdates, false);

    /// <summary>Queues work from any thread; owner-bound actions are discarded after that owner is destroyed.</summary>
    public void Post(Action action, Component? owner = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        Post(static state => ((Action)state!)(), action, owner);
    }

    internal void Post(SendOrPostCallback callback, object? state, Component? owner = null)
    {
        lock (gate)
        {
            if (!stopped) ready.Enqueue(new ReadyWork(callback, state, owner, default));
        }
    }

    internal void EnqueueContinuation(OperationLease lease)
        => ready.Enqueue(new ReadyWork(null, null, null, lease));

    internal void RequireMainThread()
    {
        if (!IsMainThread)
            throw new InvalidOperationException("Gameplay phases must run on the session's game thread.");
    }

    void RequireIdle()
    {
        RequireMainThread();
        if (draining) throw new InvalidOperationException("Gameplay phase drains cannot be reentered.");
    }

    void Drain(Queue<OperationLease> queue, bool includeReady)
    {
        RequireIdle();
        if (stopped) return;
        using var scope = EnterContext();
        draining = true;
        try
        {
            if (includeReady) DrainReady();
            var count = queue.Count;
            for (var i = 0; i < count; i++)
            {
                var lease = queue.Dequeue();
                if (!lease.IsPending) continue;
                if (!lease.Operation!.TryComplete(lease.Generation)) queue.Enqueue(lease);
            }
        }
        finally
        {
            draining = false;
            if (stopped) Dispose();
        }
    }

    void DrainReady()
    {
        var count = ready.Count;
        for (var i = 0; i < count && ready.TryDequeue(out var work); i++)
        {
            if (work.Lease.Operation is { } operation)
            {
                if (work.Lease.IsPending) operation.TryComplete(work.Lease.Generation);
                else operation.InvokeContinuation(work.Lease.Generation);
                continue;
            }
            if (stopped || IsDestroyed(work.Owner)) continue;
            try { work.Callback!(work.State); }
            catch (Exception ex) { Report(ex, work.Owner); }
        }
    }

    internal void PumpPostedWork()
    {
        RequireIdle();
        using var scope = EnterContext();
        draining = true;
        try { DrainReady(); }
        finally
        {
            draining = false;
            if (stopped) Dispose();
        }
    }

    internal double RealtimeSecondsNow => timeProvider.GetElapsedTime(origin).TotalSeconds;

    internal static bool IsDestroyed(Component? owner)
        => owner is not null && (owner.IsDestroyed || owner.Node?.IsDestroyed == true);

    internal static void Report(Exception exception, Component? owner, string? nodeName = null)
    {
        try
        {
            Log.Logger.LogError(exception, "Gameplay async failed on {Component} on {Node}",
                owner?.GetType().Name, nodeName ?? owner?.Node?.Name);
        }
        catch (Exception) { /* Reporting must not interrupt other owners' continuations. */ }
    }

    internal void Return(GameLoopOperation operation)
    {
        lock (gate)
        {
            outstanding.Remove(operation);
            operation.Clear();
            if (!stopped) pool.Push(operation);
        }
    }

    static void ValidateDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Duration must be finite and non-negative.");
    }

    readonly record struct ReadyWork(SendOrPostCallback? Callback, object? State, Component? Owner,
        OperationLease Lease);

    internal readonly record struct OperationLease(GameLoopOperation? Operation, long Generation)
    {
        internal bool IsPending => Operation?.IsPending(Generation) == true;
    }

    /// <summary>Restores the context that was active before entering gameplay execution.</summary>
    public readonly struct ContextScope : IDisposable
    {
        readonly SynchronizationContext? previous;

        internal ContextScope(SynchronizationContext next)
        {
            previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(next);
        }

        /// <summary>Restores the caller's synchronization context.</summary>
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }

    sealed class GameLoopContext(GameLoopScheduler scheduler) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => scheduler.Post(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            scheduler.RequireMainThread();
            ObjectDisposedException.ThrowIf(scheduler.IsStopped, scheduler);
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
