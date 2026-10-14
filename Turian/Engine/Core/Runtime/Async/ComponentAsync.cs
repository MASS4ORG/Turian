namespace Turian.Engine.Core;

/// <summary>Provides waits whose lifetime is bounded by one component and its game session.</summary>
public readonly struct ComponentAsync
{
    readonly Component owner;
    readonly GameLoopScheduler scheduler;

    internal ComponentAsync(Component owner, GameLoopScheduler scheduler)
    {
        this.owner = owner;
        this.scheduler = scheduler;
    }

    /// <summary>Waits until after updates on a strictly later presentation frame.</summary>
    public ValueTask NextFrame(CancellationToken cancellationToken = default)
        => scheduler.NextFrame(cancellationToken, owner);

    /// <summary>Waits for the next successfully completed simulation tick.</summary>
    public ValueTask FixedUpdate(CancellationToken cancellationToken = default)
        => scheduler.FixedUpdate(cancellationToken, owner);

    /// <summary>Waits for the next late-update boundary.</summary>
    public ValueTask LateUpdate(CancellationToken cancellationToken = default)
        => scheduler.LateUpdate(cancellationToken, owner);

    /// <summary>Waits for scaled presentation time after component updates.</summary>
    public ValueTask Seconds(double seconds, CancellationToken cancellationToken = default)
        => scheduler.Seconds(seconds, cancellationToken, owner);

    /// <summary>Waits for presentation time ignoring time scale and excluding editor pause.</summary>
    public ValueTask UnscaledSeconds(double seconds, CancellationToken cancellationToken = default)
        => scheduler.UnscaledSeconds(seconds, cancellationToken, owner);

    /// <summary>Waits for monotonic elapsed time including editor pause.</summary>
    public ValueTask RealtimeSeconds(double seconds, CancellationToken cancellationToken = default)
        => scheduler.RealtimeSeconds(seconds, cancellationToken, owner);

    /// <summary>Waits for completed fixed-tick time.</summary>
    public ValueTask FixedSeconds(double seconds, CancellationToken cancellationToken = default)
        => scheduler.FixedSeconds(seconds, cancellationToken, owner);

    /// <summary>Checks a predicate after updates until it returns true.</summary>
    public ValueTask Until(Func<bool> predicate, CancellationToken cancellationToken = default)
        => scheduler.Until(predicate, cancellationToken, owner);

    /// <summary>Checks a predicate after updates until it returns false.</summary>
    public ValueTask While(Func<bool> predicate, CancellationToken cancellationToken = default)
        => scheduler.While(predicate, cancellationToken, owner);

    /// <summary>Returns execution to this component's originating session on the game thread.</summary>
    public ValueTask MainThread(CancellationToken cancellationToken = default)
        => scheduler.MainThread(cancellationToken, owner);

    /// <summary>Starts and observes an owned routine, reporting failures without using async void.</summary>
    public void Run(Func<CancellationToken, ValueTask> routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        scheduler.RunOwned(routine, owner);
    }
}
