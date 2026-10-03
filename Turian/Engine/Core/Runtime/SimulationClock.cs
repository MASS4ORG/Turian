namespace Turian.Engine.Core;

/// <summary>
/// Drives authoritative fixed ticks independently of rendering, retaining backlog within a bounded frame budget.
/// Instances belong to one session and are driven from one thread.
/// </summary>
public sealed class SimulationClock
{
    double timeScale = 1d;
    bool stepping;
    bool started;

    /// <summary>Captures validated settings for this session without retaining the authored asset.</summary>
    public SimulationClock(TimeSettings? settings = null)
    {
        settings ??= new TimeSettings();
        ValidateDuration(settings.FixedDeltaTime, nameof(settings));
        if ((float)settings.FixedDeltaTime <= 0 || !float.IsFinite((float)settings.FixedDeltaTime) ||
            settings.MaxTicksPerFrame <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Tick interval and frame budget must be positive.");
        FixedDeltaTime = settings.FixedDeltaTime;
        MaxTicksPerFrame = settings.MaxTicksPerFrame;
    }

    /// <summary>The fixed simulation interval captured when this clock was created.</summary>
    public double FixedDeltaTime { get; }

    /// <summary>The maximum number of fixed ticks executed by one call to <see cref="Advance"/>.</summary>
    public int MaxTicksPerFrame { get; }

    /// <summary>The last successfully completed authoritative tick.</summary>
    public long TickIndex { get; private set; }

    /// <summary>Simulation time at the last completed tick, independent of render-time accumulation.</summary>
    public double ElapsedSeconds => TickIndex * FixedDeltaTime;

    /// <summary>Unconsumed simulation time, including retained catch-up backlog.</summary>
    public double AccumulatorSeconds { get; private set; }

    /// <summary>Whether a failed tick prevents further stepping and state capture.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>Freezes automatic ticks while preserving speed and accumulated time.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Multiplier for incoming simulation time; zero freezes automatic ticks and backlog.</summary>
    public double TimeScale
    {
        get => timeScale;
        set
        {
            ValidateDuration(value, nameof(value));
            timeScale = value;
        }
    }

    /// <summary>
    /// Accumulates scaled time and executes a bounded number of fixed ticks through the supplied simulation boundary.
    /// The callback receives the tick being executed and its fixed interval.
    /// </summary>
    public int Advance(double unscaledDeltaTime, Action<long, double> tick)
    {
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(tick);
        ValidateDuration(unscaledDeltaTime, nameof(unscaledDeltaTime));
        var deltaTime = IsPaused ? 0d : unscaledDeltaTime * TimeScale;
        var accumulated = AccumulatorSeconds + deltaTime;
        ValidateDuration(accumulated, nameof(unscaledDeltaTime));
        AccumulatorSeconds = accumulated;

        var count = 0;
        while (count < MaxTicksPerFrame && CanAdvance())
        {
            Step(tick);
            AccumulatorSeconds = Math.Max(0d, AccumulatorSeconds - FixedDeltaTime);
            count++;
        }
        return count;
    }

    /// <summary>Executes exactly one fixed tick regardless of pause, speed or accumulated frame time.</summary>
    public void Step(Action<long, double> tick)
    {
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(tick);
        var nextTick = checked(TickIndex + 1);
        stepping = true;
        started = true;
        try
        {
            tick(nextTick, FixedDeltaTime);
            TickIndex = nextTick;
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
        finally
        {
            stepping = false;
        }
    }

    /// <summary>Explicitly discards render-time accumulation without changing the authoritative tick.</summary>
    public void ResetAccumulator()
    {
        EnsureIdle();
        AccumulatorSeconds = 0d;
    }

    /// <summary>Captures completed-tick state for serialization; frame backlog is excluded.</summary>
    public SimulationClockState CaptureState()
    {
        EnsureIdle();
        return new SimulationClockState
        {
            TickIndex = TickIndex,
            FixedDeltaTime = FixedDeltaTime,
            TimeScale = TimeScale,
            IsPaused = IsPaused
        };
    }

    /// <summary>Restores compatible completed-tick state into a clock that has not started stepping.</summary>
    public void RestoreState(SimulationClockState state)
    {
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(state);
        if (started)
            throw new InvalidOperationException("Restore requires a fresh simulation clock.");
        ValidateState(state);
        TickIndex = state.TickIndex;
        TimeScale = state.TimeScale;
        IsPaused = state.IsPaused;
        AccumulatorSeconds = 0d;
        started = true;
    }

    // The tolerance absorbs subtraction drift at a tick boundary without depending on render frequency.
    bool CanAdvance() => !IsPaused && TimeScale > 0 &&
                         AccumulatorSeconds / FixedDeltaTime >= 1d - 1e-12;

    void EnsureIdle()
    {
        if (stepping || IsFaulted)
            throw new InvalidOperationException("The simulation clock is executing a tick or has faulted.");
    }

    void ValidateState(SimulationClockState state)
    {
        if (state.Version != SimulationClockState.CurrentVersion || state.TickIndex < 0 ||
            state.FixedDeltaTime != FixedDeltaTime)
            throw new InvalidDataException("The saved clock version, tick or fixed interval is incompatible.");
        ValidateDuration(state.TimeScale, nameof(state));
    }

    static void ValidateDuration(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, "Time values must be finite and non-negative.");
    }
}
