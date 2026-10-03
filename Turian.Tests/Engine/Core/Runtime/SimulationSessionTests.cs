namespace Turian.Tests;

/// <summary>Exercises admission, sealed batches, explicit faulting and independent scene/world state.</summary>
public sealed class SimulationSessionTests
{
    static readonly Guid Producer = Guid.Parse("b3c93e8f-27bb-41f0-b0a0-a3b6c3d1c24d");
    static JsonElement Payload(long amount = 5) => JsonSerializer.SerializeToElement(new { amount });
    static SimulationSession Create(SimulationTestWorld world, SimulationCommandLimits? limits = null) =>
        new(world, new SimulationClock(new TimeSettings { FixedDeltaTime = 0.01 }), 42, "test-v1", limits);

    /// <summary>Authority sequences order each sealed batch independently of target-tick scheduling.</summary>
    [Fact]
    public void Commands_ExecuteInTickAndAuthorityOrderAndOwnTheirPayload()
    {
        var world = new SimulationTestWorld();
        var session = Create(world);
        using (var document = JsonDocument.Parse("{\"amount\":12}"))
            Assert.Equal(SimulationCommandStatus.Accepted,
                session.Schedule(Producer, 1, "add", document.RootElement, targetTick: 2).Status);
        var earlier = session.Schedule(Producer, 2, "add", Payload(), targetTick: 1);
        var rejected = session.Schedule(Producer, 3, "unknown", Payload(), targetTick: 1);
        Assert.Equal(2, earlier.Command!.AuthoritySequence);
        Assert.Empty(world.Applied);
        session.Step();
        Assert.Equal(new long[] { 2 }, world.Applied);
        Assert.True(session.LastResults[0].Applied);
        Assert.False(session.LastResults[1].Applied);
        session.Step();
        Assert.Equal(new long[] { 2, 1 }, world.Applied);
        Assert.Single(session.LastResults);
        Assert.True(session.LastResults[0].Applied);
        Assert.Equal(12, session.LastResults[0].Command.Payload.GetProperty("amount").GetInt32());
        Assert.Equal(3, rejected.Command!.AuthoritySequence);
        session.Step();
        Assert.Empty(session.LastResults);
    }

    /// <summary>Duplicates, malformed requests and queue limits never advance a producer or authority cursor.</summary>
    [Fact]
    public void Admission_RejectsInvalidDuplicateAndOverBudgetRequests()
    {
        var limits = new SimulationCommandLimits
        { MaximumPending = 1, MaximumProducers = 1, MaximumPayloadBytes = 32, MaximumFutureTicks = 2 };
        var session = Create(new SimulationTestWorld(), limits);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Guid.Empty, 1, "add", Payload()).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 0, "add", Payload()).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, "", Payload()).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, new string('x', 129), Payload()).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, "add", Payload(), version: 0).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, "add", default).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, "add", Payload(), targetTick: 0).Status);
        Assert.Equal(SimulationCommandStatus.Invalid, session.Schedule(Producer, 1, "add", Payload(), targetTick: 3).Status);
        var large = JsonSerializer.SerializeToElement(new { text = new string('a', 33) });
        Assert.Equal(SimulationCommandStatus.LimitExceeded, session.Schedule(Producer, 1, "add", large).Status);
        var first = session.Schedule(Producer, 2, "add", Payload());
        Assert.Equal(1, first.Command!.AuthoritySequence);
        Assert.Equal(SimulationCommandStatus.DuplicateOrStale, session.Schedule(Producer, 1, "add", Payload()).Status);
        Assert.Equal(SimulationCommandStatus.DuplicateOrStale, session.Schedule(Producer, 2, "add", Payload()).Status);
        Assert.Equal(SimulationCommandStatus.LimitExceeded, session.Schedule(Producer, 3, "add", Payload()).Status);
        session.Step();
        Assert.Equal(SimulationCommandStatus.LimitExceeded, session.Schedule(Guid.NewGuid(), 1, "add", Payload()).Status);
        Assert.Equal(2, session.Schedule(Producer, 3, "add", Payload()).Command!.AuthoritySequence);
        Assert.Equal(SimulationCommandStatus.Invalid,
            session.ScheduleRecorded(first.Command with { ProducerSequence = 4 }).Status);
    }

    /// <summary>Intent generated while executing a tick cannot join the sealed batch or reenter the driver.</summary>
    [Fact]
    public void GeneratedIntent_IsDeferredAndPauseDoesNotBlockAdmission()
    {
        var world = new SimulationTestWorld();
        var session = Create(world);
        world.OnTick = tick =>
        {
            Assert.Throws<InvalidOperationException>(() => session.Step());
            Assert.Throws<InvalidOperationException>(() => session.Advance(0));
            Assert.Throws<InvalidOperationException>(() => session.CaptureState());
            if (tick != 1) return;
            Assert.Equal(SimulationCommandStatus.Invalid,
                session.Schedule(Producer, 2, "add", Payload(), targetTick: tick).Status);
            Assert.Equal(2, session.Schedule(Producer, 2, "add", Payload()).Command!.TargetTick);
        };
        session.Clock.IsPaused = true;
        Assert.Equal(SimulationCommandStatus.Accepted, session.Schedule(Producer, 1, "unknown", Payload()).Status);
        Assert.Equal(0, session.Advance(1));
        session.Step();
        Assert.Empty(world.Applied);
        session.Step();
        Assert.Single(world.Applied);
    }

    /// <summary>Unexpected command or system failures fault the clock without completing or publishing the tick.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failure_PreventsFurtherTicksAndCheckpointCapture(bool commandFailure)
    {
        var world = new SimulationTestWorld();
        var session = Create(world);
        if (commandFailure)
            session.Schedule(Producer, 1, "add", JsonSerializer.SerializeToElement(new { wrong = 1 }));
        else
            world.OnTick = _ => throw new InvalidOperationException("system failure");
        Assert.ThrowsAny<Exception>(() => session.Step());
        Assert.True(session.Clock.IsFaulted);
        Assert.Equal(0, session.Clock.TickIndex);
        Assert.Empty(session.LastResults);
        Assert.Throws<InvalidOperationException>(() => session.CaptureState());
        Assert.Throws<InvalidOperationException>(() => session.ComputeStateHash());
        Assert.Throws<InvalidOperationException>(() => session.Advance(0));
        Assert.Throws<InvalidOperationException>(() => session.Schedule(Producer, 2, "add", Payload()));
    }

    /// <summary>Scene presentation and the authoritative world use one clock and execute one fixed batch.</summary>
    [Fact]
    public void SceneTicker_DrivesOneSharedSessionAndRejectsASecondClock()
    {
        var world = new SimulationTestWorld();
        var session = Create(world);
        var scenes = new SceneManager(new AssetDatabase());
        var ticker = new SceneTicker(scenes, session.Clock) { Simulation = session };
        session.Schedule(Producer, 1, "add", Payload());
        ticker.Tick(0.02);
        Assert.Equal(2, session.Clock.TickIndex);
        Assert.Single(world.Applied);
        session.Clock.IsPaused = true;
        ticker.Tick(1);
        ticker.StepFrame();
        Assert.Equal(3, session.CaptureState().Clock.TickIndex);
        var wrong = new SceneTicker(scenes) { Simulation = session };
        Assert.Throws<InvalidOperationException>(() => wrong.StepFrame());
        Assert.True(wrong.Clock.IsFaulted);
    }

    /// <summary>Capture callbacks cannot enqueue commands, and direct clock driving cannot bypass the world.</summary>
    [Fact]
    public void Boundary_RejectsMutationDuringCaptureAndClockBypass()
    {
        var world = new SimulationTestWorld();
        var session = Create(world);
        world.OnCapture = () =>
        {
            Assert.Throws<InvalidOperationException>(() => session.CaptureState());
            Assert.Throws<InvalidOperationException>(() => session.Schedule(Producer, 1, "add", Payload()));
        };
        Assert.Equal(0, session.CaptureState().Clock.TickIndex);
        session.Clock.Step((_, _) => { });
        Assert.Throws<InvalidOperationException>(() => session.Step());
        Assert.Throws<InvalidOperationException>(() => session.CaptureState());
        Assert.Throws<ArgumentNullException>(() => Create(new SimulationTestWorld()).ScheduleRecorded(null!));
    }

    /// <summary>Observers receive every completed catch-up tick and can capture, queue intent and pause without reentry.</summary>
    [Fact]
    public void CompletedTicks_PublishCommittedStateAndResultsDuringCatchUp()
    {
        var session = Create(new SimulationTestWorld());
        session.Schedule(Producer, 1, "add", Payload());
        var observed = new List<long>();
        session.TickCompleted += current =>
        {
            var tick = current.Clock.TickIndex;
            observed.Add(tick);
            Assert.Equal(tick, current.CaptureState().Clock.TickIndex);
            Assert.Single(current.LastResults);
            Assert.Throws<InvalidOperationException>(() => current.Step());
            Assert.Throws<InvalidOperationException>(() => current.Advance(0));
            if (tick == 1)
                Assert.Equal(2, current.Schedule(Producer, 2, "add", Payload()).Command!.TargetTick);
            else
                current.Clock.IsPaused = true;
        };
        Assert.Equal(2, session.Advance(0.03));
        Assert.Equal(new long[] { 1, 2 }, observed);
        Assert.Equal(0.01, session.Clock.AccumulatorSeconds, 12);
        Assert.False(session.Clock.IsFaulted);
    }

    /// <summary>A failed publication blocks further simulation while preserving the completed clock boundary.</summary>
    [Fact]
    public void ObserverFailure_FaultsSessionAfterCommittingTheTick()
    {
        var session = Create(new SimulationTestWorld());
        session.TickCompleted += _ => throw new InvalidOperationException("observer failure");
        Assert.Throws<InvalidOperationException>(() => session.Advance(0.02));
        Assert.Equal(1, session.Clock.TickIndex);
        Assert.Equal(0.01, session.Clock.AccumulatorSeconds, 12);
        Assert.Throws<InvalidOperationException>(() => session.Step());
        Assert.Throws<InvalidOperationException>(() => session.CaptureState());
    }

    /// <summary>Counter exhaustion rejects admission instead of wrapping identities or scheduling a past tick.</summary>
    [Fact]
    public void Admission_RejectsExhaustedAuthorityAndTickCounters()
    {
        var source = Create(new SimulationTestWorld()).CaptureState();
        var exhausted = Create(new SimulationTestWorld());
        exhausted.RestoreState(source with
        { Commands = source.Commands with { LastAuthoritySequence = long.MaxValue } });
        Assert.Equal(SimulationCommandStatus.LimitExceeded, exhausted.Schedule(Producer, 1, "add", Payload()).Status);
        var atLastTick = Create(new SimulationTestWorld());
        atLastTick.RestoreState(source with { Clock = source.Clock with { TickIndex = long.MaxValue } });
        Assert.Equal(SimulationCommandStatus.LimitExceeded, atLastTick.Schedule(Producer, 1, "add", Payload()).Status);
    }

    /// <summary>Invalid constructor arguments and each resource limit are rejected before a session can run.</summary>
    [Fact]
    public void Constructor_ValidatesConfiguration()
    {
        var clock = new SimulationClock();
        Assert.Throws<ArgumentNullException>(() => new SimulationSession(null!, clock, 1, "v1"));
        Assert.Throws<ArgumentNullException>(() => new SimulationSession(new SimulationTestWorld(), null!, 1, "v1"));
        Assert.Throws<ArgumentException>(() => new SimulationSession(new SimulationTestWorld(), clock, 1, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SimulationTestWorld(), new() { MaximumPending = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SimulationTestWorld(), new() { MaximumProducers = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SimulationTestWorld(), new() { MaximumPayloadBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SimulationTestWorld(), new() { MaximumFutureTicks = 0 }));
        clock.Step((_, _) => { });
        Assert.Throws<ArgumentException>(() => new SimulationSession(new SimulationTestWorld(), clock, 1, "v1"));
    }
}
