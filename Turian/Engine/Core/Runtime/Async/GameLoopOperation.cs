namespace Turian.Engine.Core;

/// <summary>Stores one pooled wait result while the scheduler controls continuation dispatch.</summary>
sealed class GameLoopOperation(GameLoopScheduler scheduler) : System.Threading.Tasks.Sources.IValueTaskSource
{
    readonly object gate = new();
    System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> core;
    GameLoopWait kind;
    double deadline;
    Func<bool>? predicate;
    CancellationToken token;
    Component? owner;
    Action<object?>? continuation;
    object? state;
    ExecutionContext? executionContext;
    bool completed;
    bool registered;
    bool consumed;
    long lastPredicateFrame;
    string? creationStack;
    string? ownerNode;
    string? ownerComponent;

    internal long Generation { get; private set; }
    internal ValueTask Value => new(this, core.Version);

    internal void Initialize(long generation, GameLoopWait wait, double due, Func<bool>? condition,
        CancellationToken cancellationToken, Component? component)
    {
        lock (gate)
        {
            core.Reset();
            Generation = generation;
            kind = wait;
            deadline = due;
            predicate = condition;
            token = cancellationToken;
            owner = component;
            ownerNode = component?.Node?.Name;
            ownerComponent = component?.GetType().Name;
            completed = registered = consumed = false;
            lastPredicateFrame = -1;
            creationStack = scheduler.CaptureCreationStacks ? Environment.StackTrace : null;
        }
    }

    internal bool IsPending(long generation)
    {
        lock (gate)
        {
            return Generation == generation && !consumed && !completed;
        }
    }

    internal bool TryCancel(long generation)
    {
        lock (gate)
        {
            if (Generation != generation || consumed)
            {
                return false;
            }
        }

        var cancellation = scheduler.Cancellation(token, owner);
        if (!cancellation.IsCancellationRequested)
        {
            return false;
        }

        Complete(generation, new OperationCanceledException(cancellation));
        return true;
    }

    internal bool TryComplete(long generation)
    {
        if (TryCancel(generation))
        {
            return true;
        }

        bool eligible;
        try { eligible = IsEligible(); }
        catch (Exception ex)
        {
            Complete(generation, ex);
            return true;
        }
        if (!eligible)
        {
            return false;
        }

        Complete(generation, null);
        return true;
    }

    bool IsEligible() => kind switch
    {
        GameLoopWait.NextFrame => scheduler.FrameIndex >= deadline,
        GameLoopWait.FixedUpdate => scheduler.FixedIndex >= deadline,
        GameLoopWait.Seconds => scheduler.ElapsedSeconds >= deadline,
        GameLoopWait.UnscaledSeconds => scheduler.UnscaledElapsedSeconds >= deadline,
        GameLoopWait.RealtimeSeconds => scheduler.RealtimeSecondsNow >= deadline,
        GameLoopWait.FixedSeconds => scheduler.FixedElapsedSeconds >= deadline,
        GameLoopWait.Until => PollPredicate(true),
        GameLoopWait.While => PollPredicate(false),
        _ => true
    };

    bool PollPredicate(bool desired)
    {
        if (lastPredicateFrame == scheduler.FrameIndex)
        {
            return false;
        }

        lastPredicateFrame = scheduler.FrameIndex;
        return predicate!() == desired;
    }

    void Complete(long generation, Exception? exception)
    {
        scheduler.RequireMainThread();
        lock (gate)
        {
            if (Generation != generation || consumed)
            {
                return;
            }

            if (!completed)
            {
                completed = true;
                if (exception is null)
                {
                    core.SetResult(true);
                }
                else
                {
                    core.SetException(exception);
                }
            }
        }
        InvokeContinuation(generation);
    }

    internal void InvokeContinuation(long generation)
    {
        scheduler.RequireMainThread();
        Action<object?>? callback;
        object? callbackState;
        ExecutionContext? captured;
        Component? component;
        string? nodeName;
        lock (gate)
        {
            if (Generation != generation || !completed || consumed || continuation is null)
            {
                return;
            }

            callback = continuation;
            callbackState = state;
            captured = executionContext;
            component = owner;
            nodeName = ownerNode;
            continuation = null;
            state = null;
            executionContext = null;
        }
        try
        {
            if (captured is null)
            {
                callback(callbackState);
            }
            else
            {
                ExecutionContext.Run(captured, static payload =>
            {
                var invocation = ((Action<object?> Callback, object? State))payload!;
                invocation.Callback(invocation.State);
            }, (callback, callbackState));
            }
        }
        catch (Exception ex) { GameLoopScheduler.Report(ex, component, nodeName); }
    }

    /// <inheritdoc/>
    public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short version)
    {
        lock (gate)
        {
            Validate(version);
            return core.GetStatus(version);
        }
    }

    /// <inheritdoc/>
    public void GetResult(short version)
    {
        lock (gate)
        {
            Validate(version);
            if (!completed)
            {
                throw new InvalidOperationException("A gameplay wait cannot be consumed before completion.");
            }

            consumed = true;
        }
        try
        {
            core.GetResult(version);
            scheduler.Cancellation(token, owner).ThrowIfCancellationRequested();
        }
        finally { scheduler.Return(this); }
    }

    /// <inheritdoc/>
    public void OnCompleted(Action<object?> callback, object? callbackState, short version,
        System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (gate)
        {
            Validate(version);
            if (registered)
            {
                throw new InvalidOperationException("A gameplay wait can only be awaited once.");
            }

            registered = true;
            continuation = callback;
            state = callbackState;
            if ((flags & System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            {
                executionContext = ExecutionContext.Capture();
            }

            if (completed)
            {
                scheduler.EnqueueContinuation(new GameLoopScheduler.OperationLease(this, Generation));
            }
        }
    }

    void Validate(short version)
    {
        if (consumed || version != core.Version)
        {
            throw new InvalidOperationException("A gameplay wait must be consumed exactly once; preserve shared results.");
        }
    }

    internal GameLoopOperationInfo Inspect()
    {
        lock (gate)
        {
            return new GameLoopOperationInfo(kind.ToString(), ownerComponent, ownerNode,
            completed, creationStack);
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            owner = null;
            predicate = null;
            continuation = null;
            state = null;
            executionContext = null;
            creationStack = null;
            ownerNode = null;
            ownerComponent = null;
            token = default;
        }
    }
}
