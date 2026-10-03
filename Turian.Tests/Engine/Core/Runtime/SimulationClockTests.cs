namespace Turian.Tests;

/// <summary>Headless checks for fixed-tick scheduling, overload, fault isolation and clock-state resume.</summary>
public sealed class SimulationClockTests
{
    static void IgnoreTick(long tick, double interval) { }

    /// <summary>Different rendering schedules execute the same ordered authoritative ticks.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void Advance_IsIndependentOfRenderRate(int renderRate)
    {
        var clock = new SimulationClock();
        var ticks = new List<long>();
        for (var frame = 0; frame < renderRate * 2; frame++)
            clock.Advance(1d / renderRate, (tick, interval) =>
            {
                Assert.Equal(tick - 1, clock.TickIndex);
                Assert.Equal(SceneTicker.FixedTimestep, interval);
                ticks.Add(tick);
            });

        Assert.Equal(Enumerable.Range(1, 120).Select(tick => (long)tick), ticks);
        Assert.Equal(120, clock.TickIndex);
        Assert.Equal(2d, clock.ElapsedSeconds);
        Assert.InRange(clock.AccumulatorSeconds, 0d, 1e-12);
    }

    /// <summary>Catch-up retains excess time and drains it without dropping or duplicating ticks.</summary>
    [Fact]
    public void Advance_BoundsCatchUpAndRetainsBacklog()
    {
        var settings = new TimeSettings { FixedDeltaTime = 0.01, MaxTicksPerFrame = 2 };
        var clock = new SimulationClock(settings);
        settings.FixedDeltaTime = 1;
        settings.MaxTicksPerFrame = 100;

        Assert.Equal(2, clock.Advance(0.1, IgnoreTick));
        Assert.Equal(0.08, clock.AccumulatorSeconds, 12);
        Assert.Equal(0.01, clock.FixedDeltaTime);
        Assert.Equal(2, clock.MaxTicksPerFrame);

        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(2, clock.Advance(0, IgnoreTick));

        Assert.Equal(10, clock.TickIndex);
        Assert.Equal(0, clock.Advance(0, IgnoreTick));
    }

    /// <summary>Pause and zero speed preserve backlog; explicit stepping advances one completed tick.</summary>
    [Fact]
    public void PauseAndStep_PreserveSpeedAndBacklog()
    {
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = 0.01, MaxTicksPerFrame = 2 });
        clock.Advance(0.1, IgnoreTick);
        var backlog = clock.AccumulatorSeconds;
        clock.TimeScale = 4;
        clock.IsPaused = true;

        Assert.Equal(0, clock.Advance(100, IgnoreTick));
        Assert.Equal(backlog, clock.AccumulatorSeconds);
        clock.Step(IgnoreTick);
        Assert.Equal(3, clock.TickIndex);
        Assert.Equal(backlog, clock.AccumulatorSeconds);
        Assert.Equal(4, clock.TimeScale);

        clock.IsPaused = false;
        clock.TimeScale = 0;
        Assert.Equal(0, clock.Advance(100, IgnoreTick));
        Assert.Equal(backlog, clock.AccumulatorSeconds);
        clock.TimeScale = 4;
        Assert.Equal(2, clock.Advance(0, IgnoreTick));
        Assert.Equal(5, clock.TickIndex);
    }

    /// <summary>Speed changes affect new time while pause is honored between catch-up ticks.</summary>
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 8)]
    public void Advance_AppliesTimeScale(double speed, int expectedTicks)
    {
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = 0.01 }) { TimeScale = speed };

        Assert.Equal(expectedTicks, clock.Advance(0.02, IgnoreTick));
        clock.Advance(0.02, (_, _) => clock.IsPaused = true);
        Assert.Equal(expectedTicks + 1, clock.TickIndex);
    }

    /// <summary>A failed simulation boundary leaves its tick incomplete and prevents publishing partial state.</summary>
    [Fact]
    public void FaultedTick_CannotAdvanceOrCaptureState()
    {
        var clock = new SimulationClock();

        Assert.Throws<InvalidOperationException>(() => clock.Advance(1, (_, _) =>
            throw new InvalidOperationException("system failure")));

        Assert.True(clock.IsFaulted);
        Assert.Equal(0, clock.TickIndex);
        Assert.Throws<InvalidOperationException>(() => clock.Step(IgnoreTick));
        Assert.Throws<InvalidOperationException>(() => clock.Advance(0, IgnoreTick));
        Assert.Throws<InvalidOperationException>(() => clock.CaptureState());
        Assert.Throws<InvalidOperationException>(() => clock.ResetAccumulator());
    }

    /// <summary>Reentrant stepping and capture during a tick cannot expose incomplete state.</summary>
    [Fact]
    public void Step_RejectsReentrancyAndCaptureDuringExecution()
    {
        var clock = new SimulationClock();
        clock.Step((_, _) =>
        {
            Assert.Throws<InvalidOperationException>(() => clock.Step(IgnoreTick));
            Assert.Throws<InvalidOperationException>(() => clock.Advance(0, IgnoreTick));
            Assert.Throws<InvalidOperationException>(() => clock.CaptureState());
            Assert.Throws<InvalidOperationException>(() => clock.RestoreState(new SimulationClockState()));
            Assert.Throws<InvalidOperationException>(() => clock.ResetAccumulator());
        });

        Assert.False(clock.IsFaulted);
        Assert.Equal(1, clock.TickIndex);
    }

    /// <summary>Clock JSON restores the completed boundary while discarding render-time backlog.</summary>
    [Fact]
    public void SaveResume_RestoresClockStateIntoAFreshSession()
    {
        var settings = new TimeSettings { FixedDeltaTime = 0.01, MaxTicksPerFrame = 2 };
        var original = new SimulationClock(settings) { TimeScale = 2 };
        original.Advance(0.1, IgnoreTick);
        original.IsPaused = true;
        var state = original.CaptureState();
        var json = JsonSerializer.Serialize(state);
        var saved = JsonSerializer.Deserialize<SimulationClockState>(json)!;
        var resumed = new SimulationClock(settings);

        resumed.RestoreState(saved);

        Assert.Equal(state, resumed.CaptureState());
        Assert.Equal(0, resumed.AccumulatorSeconds);
        Assert.Equal(0.02, resumed.ElapsedSeconds);
        Assert.Equal(0, resumed.Advance(1, IgnoreTick));
        resumed.IsPaused = false;
        Assert.Equal(2, resumed.Advance(0.01, IgnoreTick));
        Assert.Equal(4, resumed.TickIndex);
        Assert.Throws<InvalidOperationException>(() => resumed.RestoreState(saved));
        Assert.Throws<InvalidOperationException>(() => original.RestoreState(saved));
        Assert.Throws<ArgumentNullException>(() => new SimulationClock().RestoreState(null!));
    }

    /// <summary>Invalid checkpoint metadata fails before any clock fields or backlog are changed.</summary>
    [Fact]
    public void Restore_ValidatesBeforeMutatingState()
    {
        var clock = new SimulationClock();
        clock.Advance(0.001, IgnoreTick);
        var initial = clock.CaptureState();
        var saved = initial with { TickIndex = 20, TimeScale = 4, IsPaused = true };

        Assert.Throws<InvalidDataException>(() => clock.RestoreState(saved with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => clock.RestoreState(saved with { TickIndex = -1 }));
        Assert.Throws<InvalidDataException>(() => clock.RestoreState(saved with { FixedDeltaTime = 0.1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.RestoreState(saved with { TimeScale = double.NaN }));
        Assert.Equal(initial, clock.CaptureState());
        Assert.Equal(0.001, clock.AccumulatorSeconds);

        var incomplete = JsonNode.Parse(JsonSerializer.Serialize(saved))!.AsObject();
        incomplete.Remove(nameof(SimulationClockState.TickIndex));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SimulationClockState>(incomplete.ToJsonString()));
    }

    /// <summary>Only an explicit accumulator reset discards unconsumed time.</summary>
    [Fact]
    public void ResetAccumulator_PreservesCompletedTick()
    {
        var clock = new SimulationClock(new TimeSettings { MaxTicksPerFrame = 1 });
        clock.Advance(1, IgnoreTick);

        clock.ResetAccumulator();

        Assert.Equal(1, clock.TickIndex);
        Assert.Equal(0, clock.AccumulatorSeconds);
        Assert.Equal(0, clock.Advance(0, IgnoreTick));
    }

    /// <summary>Invalid settings cannot create a clock or enter its fixed-step driver.</summary>
    [Theory]
    [InlineData(0, 8)]
    [InlineData(-1, 8)]
    [InlineData(double.NaN, 8)]
    [InlineData(double.PositiveInfinity, 8)]
    [InlineData(double.Epsilon, 8)]
    [InlineData(double.MaxValue, 8)]
    [InlineData(0.01, 0)]
    [InlineData(0.01, -1)]
    public void Constructor_RejectsInvalidSettings(double interval, int budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SimulationClock(new TimeSettings { FixedDeltaTime = interval, MaxTicksPerFrame = budget }));
    }

    /// <summary>Invalid durations, speed and arithmetic overflow are rejected before accumulating time.</summary>
    [Fact]
    public void Advance_RejectsInvalidTimeAndOverflow()
    {
        var clock = new SimulationClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1, IgnoreTick));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(double.NaN, IgnoreTick));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(double.PositiveInfinity, IgnoreTick));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TimeScale = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TimeScale = double.PositiveInfinity);
        Assert.Throws<ArgumentNullException>(() => clock.Advance(0, null!));
        Assert.Throws<ArgumentNullException>(() => clock.Step(null!));
        clock.TimeScale = 2;
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(double.MaxValue, IgnoreTick));
        Assert.Equal(0, clock.AccumulatorSeconds);
        clock.TimeScale = 1;
        clock.Advance(double.MaxValue, IgnoreTick);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(double.MaxValue, IgnoreTick));
        Assert.Equal(double.MaxValue, clock.AccumulatorSeconds);
    }

    /// <summary>A tick counter overflow cannot invoke another simulation boundary.</summary>
    [Fact]
    public void Step_RejectsTickIndexOverflowBeforeCallingGameCode()
    {
        var clock = new SimulationClock();
        clock.RestoreState(clock.CaptureState() with { TickIndex = long.MaxValue });

        Assert.Throws<OverflowException>(() => clock.Step((_, _) => Assert.Fail("Unexpected tick")));

        Assert.Equal(long.MaxValue, clock.TickIndex);
        Assert.False(clock.IsFaulted);
    }

    /// <summary>An irregular render schedule and fresh-session resume preserve the same integer simulation state.</summary>
    [Fact]
    public void Advance_ResumesUnderADifferentRenderingSchedule()
    {
        var settings = new TimeSettings { FixedDeltaTime = 0.01, MaxTicksPerFrame = 8 };
        var uninterrupted = new SimulationClock(settings);
        long expected = 0;
        for (var frame = 0; frame < 100; frame++)
            uninterrupted.Advance(0.01, (tick, _) => expected += tick);

        var first = new SimulationClock(settings);
        long actual = 0;
        for (var frame = 0; frame < 50; frame++)
            first.Advance(0.01, (tick, _) => actual += tick);

        var restored = new SimulationClock(settings);
        var json = JsonSerializer.Serialize(first.CaptureState());
        restored.RestoreState(JsonSerializer.Deserialize<SimulationClockState>(json)!);
        for (var frame = 0; frame < 10; frame++)
        {
            restored.Advance(0.007, (tick, _) => actual += tick);
            restored.Advance(0.043, (tick, _) => actual += tick);
        }

        Assert.Equal(uninterrupted.CaptureState(), restored.CaptureState());
        Assert.Equal(expected, actual);
    }
}
