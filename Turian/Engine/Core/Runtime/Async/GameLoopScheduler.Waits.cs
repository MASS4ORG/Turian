namespace Turian.Engine.Core;

public sealed partial class GameLoopScheduler
{
    /// <summary>Waits for a strictly later presentation frame, resuming after component updates.</summary>
    public ValueTask NextFrame(CancellationToken cancellationToken = default, Component? owner = null)
        => Schedule(GameLoopWait.NextFrame, FrameIndex + 1, null, cancellationToken, owner, updates);

    /// <summary>Waits for the next successfully completed fixed tick, including catch-up ticks in this frame.</summary>
    public ValueTask FixedUpdate(CancellationToken cancellationToken = default, Component? owner = null)
        => Schedule(GameLoopWait.FixedUpdate, FixedIndex + 1, null, cancellationToken, owner, fixedUpdates);

    /// <summary>Waits for the next late-update boundary.</summary>
    public ValueTask LateUpdate(CancellationToken cancellationToken = default, Component? owner = null)
        => Schedule(GameLoopWait.LateUpdate, 0, null, cancellationToken, owner, lateUpdates);

    /// <summary>Waits for scaled presentation time, resuming after updates rather than inside fixed catch-up.</summary>
    public ValueTask Seconds(double seconds, CancellationToken cancellationToken = default, Component? owner = null)
        => Delay(GameLoopWait.Seconds, seconds, ElapsedSeconds, cancellationToken, owner, updates);

    /// <summary>Waits for presentation time ignoring time scale; hosts freeze it by not advancing paused frames.</summary>
    public ValueTask UnscaledSeconds(double seconds, CancellationToken cancellationToken = default,
        Component? owner = null)
        => Delay(GameLoopWait.UnscaledSeconds, seconds, UnscaledElapsedSeconds, cancellationToken, owner, updates);

    /// <summary>Waits for monotonic elapsed time, including editor pauses; execution resumes on an eligible frame.</summary>
    public ValueTask RealtimeSeconds(double seconds, CancellationToken cancellationToken = default,
        Component? owner = null)
        => Delay(GameLoopWait.RealtimeSeconds, seconds, RealtimeSecondsNow, cancellationToken, owner, updates);

    /// <summary>Waits for completed simulation time, resuming at the first eligible fixed boundary.</summary>
    public ValueTask FixedSeconds(double seconds, CancellationToken cancellationToken = default,
        Component? owner = null)
        => Delay(GameLoopWait.FixedSeconds, seconds, FixedElapsedSeconds, cancellationToken, owner, fixedUpdates);

    /// <summary>Checks the predicate once per update boundary until it returns true.</summary>
    public ValueTask Until(Func<bool> predicate, CancellationToken cancellationToken = default, Component? owner = null)
        => Predicate(GameLoopWait.Until, predicate, cancellationToken, owner);

    /// <summary>Checks the predicate once per update boundary until it returns false.</summary>
    public ValueTask While(Func<bool> predicate, CancellationToken cancellationToken = default, Component? owner = null)
        => Predicate(GameLoopWait.While, predicate, cancellationToken, owner);

    /// <summary>Marshals execution from any thread to the originating session's next update drain.</summary>
    public ValueTask MainThread(CancellationToken cancellationToken = default, Component? owner = null)
    {
        lock (gate)
        {
            var canceled = Cancellation(cancellationToken, owner);
            if (canceled.IsCancellationRequested) return ValueTask.FromCanceled(canceled);
            var operation = Rent(GameLoopWait.MainThread, 0, null, cancellationToken, owner);
            EnqueueContinuation(new OperationLease(operation, operation.Generation));
            return operation.Value;
        }
    }

    ValueTask Predicate(GameLoopWait kind, Func<bool> predicate, CancellationToken token, Component? owner)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Schedule(kind, 0, predicate, token, owner, updates);
    }

    ValueTask Delay(GameLoopWait kind, double seconds, double now, CancellationToken token, Component? owner,
        Queue<OperationLease> queue)
    {
        ValidateDuration(seconds);
        var deadline = now + seconds;
        ValidateDuration(deadline);
        return Schedule(kind, deadline, null, token, owner, queue);
    }

    ValueTask Schedule(GameLoopWait kind, double deadline, Func<bool>? predicate, CancellationToken token,
        Component? owner, Queue<OperationLease> queue)
    {
        RequireMainThread();
        lock (gate)
        {
            var canceled = Cancellation(token, owner);
            if (canceled.IsCancellationRequested) return ValueTask.FromCanceled(canceled);
            var operation = Rent(kind, deadline, predicate, token, owner);
            queue.Enqueue(new OperationLease(operation, operation.Generation));
            return operation.Value;
        }
    }

    GameLoopOperation Rent(GameLoopWait kind, double deadline, Func<bool>? predicate, CancellationToken token,
        Component? owner)
    {
        var operation = pool.TryPop(out var available) ? available : new GameLoopOperation(this);
        operation.Initialize(++generation, kind, deadline, predicate, token, owner);
        outstanding.Add(operation);
        return operation;
    }

    internal CancellationToken Cancellation(CancellationToken token, Component? owner)
    {
        if (token.IsCancellationRequested) return token;
        if (stopped) return sessionToken;
        if (owner is not null && owner.DestroyCancellationToken.IsCancellationRequested)
            return owner.DestroyCancellationToken;
        return IsDestroyed(owner) ? new CancellationToken(true) : default;
    }

    /// <summary>Cancels dead owners and caller-canceled waits without advancing any gameplay clocks or phases.</summary>
    public void PumpCancellation()
    {
        RequireIdle();
        using var scope = EnterContext();
        draining = true;
        try
        {
            lock (gate)
            {
                cancellationSnapshot.Clear();
                foreach (var operation in outstanding)
                    cancellationSnapshot.Add(new OperationLease(operation, operation.Generation));
            }
            foreach (var lease in cancellationSnapshot)
                lease.Operation!.TryCancel(lease.Generation);
        }
        finally
        {
            cancellationSnapshot.Clear();
            draining = false;
            if (stopped) Dispose();
        }
    }

    /// <summary>Returns a diagnostic snapshot, including completed operations that have not been consumed.</summary>
    public IReadOnlyList<GameLoopOperationInfo> InspectOutstanding()
    {
        lock (gate) return outstanding.Select(operation => operation.Inspect()).ToArray();
    }

    /// <summary>Stops the session and unwinds owned waits on the game thread before their services are released.</summary>
    public void Dispose()
    {
        RequireMainThread();
        bool cancel;
        lock (gate)
        {
            if (shuttingDown) return;
            cancel = !stopped;
            stopped = true;
        }
        if (cancel)
        {
            try { lifetime.Cancel(); }
            catch (Exception ex) { Report(ex, null); }
        }
        if (draining) return;
        shuttingDown = true;
        PumpCancellation();
        DrainStoppedContinuations();
        updates.Clear();
        fixedUpdates.Clear();
        lateUpdates.Clear();
        pool.Clear();
        lifetime.Dispose();
        foreach (var operation in InspectOutstanding())
        {
            try
            {
                Log.Logger.LogWarning("Unconsumed gameplay wait {Kind} on {Component} on {Node}: {CreationStack}",
                    operation.Kind, operation.Component, operation.Node, operation.CreationStack);
            }
            catch (Exception) { /* Diagnostics must not prevent session teardown. */ }
        }
    }

    void DrainStoppedContinuations()
    {
        using var scope = EnterContext();
        draining = true;
        try
        {
            while (ready.TryDequeue(out var work))
                if (work.Lease.Operation is { } operation)
                    operation.InvokeContinuation(work.Lease.Generation);
        }
        finally { draining = false; }
    }
}

/// <summary>Describes an outstanding wait without exposing its pooled source.</summary>
public sealed record GameLoopOperationInfo(string Kind, string? Component, string? Node, bool IsCompleted,
    string? CreationStack);

enum GameLoopWait
{
    NextFrame,
    FixedUpdate,
    LateUpdate,
    Seconds,
    UnscaledSeconds,
    RealtimeSeconds,
    FixedSeconds,
    Until,
    While,
    MainThread
}
