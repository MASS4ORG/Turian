namespace Turian.Engine.Core;

/// <summary>Owns authoritative commands, random streams and a game world driven by one shared clock.</summary>
public sealed class SimulationSession
{
    readonly ISimulationWorld world;
    readonly SimulationCommandLimits? limits;
    SimulationCommandQueue commands;
    long executingTick;
    long completedTick;
    bool capturing;
    bool started;
    bool faulted;
    bool publishing;

    /// <summary>Creates a session; compatibility identifies the supported game build, content and simulation schemas.</summary>
    public SimulationSession(ISimulationWorld world, SimulationClock clock, ulong seed, string compatibility,
        SimulationCommandLimits? limits = null)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibility);
        if (clock.TickIndex != 0) throw new ArgumentException("The session requires a fresh clock.", nameof(clock));
        Compatibility = compatibility;
        this.limits = limits;
        commands = new SimulationCommandQueue(limits);
        Random = new SimulationRandomStreams(seed);
        _ = Random.Get("default");
    }

    /// <summary>The clock shared with the scene driver; authoritative ticks must pass through this session.</summary>
    public SimulationClock Clock { get; }

    /// <summary>The game-owned simulation compatibility identifier.</summary>
    public string Compatibility { get; }

    /// <summary>The authoritative streams; presentation must use a separate random source.</summary>
    public SimulationRandomStreams Random { get; private set; }

    /// <summary>The completed tick's command results; consume or record them before the next tick.</summary>
    public IReadOnlyList<SimulationCommandResult> LastResults { get; private set; } = [];

    /// <summary>Raised once per completed tick, including catch-up ticks; capture and admission are allowed, reentry is not.</summary>
    public event Action<SimulationSession>? TickCompleted;

    /// <summary>Schedules a trusted producer's intent; authentication and permissions belong to the authority adapter.</summary>
    public SimulationCommandAdmission Schedule(Guid producer, long producerSequence, string kind, JsonElement payload,
        int version = 1, long? targetTick = null)
    {
        EnsureAdmission();
        var boundary = executingTick == 0 ? Clock.TickIndex : executingTick;
        if (boundary == long.MaxValue) return new(SimulationCommandStatus.LimitExceeded, null);
        var result = commands.Schedule(producer, producerSequence, kind, version, payload,
            targetTick ?? boundary + 1, boundary);
        started |= result.Status == SimulationCommandStatus.Accepted;
        return result;
    }

    /// <summary>Replays trusted recorded authority decisions in admission-sequence order; never use for client requests.</summary>
    public SimulationCommandAdmission ScheduleRecorded(SimulationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAdmission();
        var result = commands.Accept(command, executingTick == 0 ? Clock.TickIndex : executingTick);
        started |= result.Status == SimulationCommandStatus.Accepted;
        return result;
    }

    /// <summary>Drives a headless world with the shared clock's bounded catch-up policy.</summary>
    public int Advance(double unscaledDeltaTime)
    {
        EnsureDrivingBoundary();
        return Clock.AdvanceWithCompletion(unscaledDeltaTime, RunTick, PublishCompletedTick);
    }

    /// <summary>Executes one authoritative tick regardless of pause and speed.</summary>
    public void Step()
    {
        EnsureDrivingBoundary();
        Clock.Step(RunTick);
        PublishCompletedTick();
    }

    internal void RunTick(long tick, double interval)
    {
        EnsureDrivingBoundary();
        if (tick != checked(completedTick + 1) || interval != Clock.FixedDeltaTime)
            throw new InvalidOperationException("The session must be driven by its own clock.");
        executingTick = tick;
        started = true;
        try
        {
            var batch = commands.Seal(tick);
            var results = batch.Select(command =>
                new SimulationCommandResult(command, world.ApplyCommand(command, Random))).ToArray();
            world.Tick(tick, interval, Random);
            LastResults = Array.AsReadOnly(results);
            completedTick = tick;
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            executingTick = 0;
        }
    }

    internal void PublishCompletedTick()
    {
        publishing = true;
        try
        {
            TickCompleted?.Invoke(this);
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            publishing = false;
        }
    }

    /// <summary>Captures a completed boundary; game capture must be pure and exclude presentation state.</summary>
    public SimulationCheckpoint CaptureState()
    {
        EnsureBoundary();
        capturing = true;
        try
        {
            var state = world.CaptureState();
            if (state.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Simulation world state must be a JSON object.");
            return new SimulationCheckpoint
            {
                Compatibility = Compatibility,
                Clock = Clock.CaptureState(),
                Random = Random.CaptureState(),
                Commands = commands.CaptureState(),
                World = state.Clone()
            };
        }
        finally
        {
            capturing = false;
        }
    }

    /// <summary>Restores validated metadata into an unused session; a failed game restore faults the session.</summary>
    public void RestoreState(SimulationCheckpoint state)
    {
        EnsureBoundary();
        ArgumentNullException.ThrowIfNull(state);
        if (started) throw new InvalidOperationException("Restore requires an unused simulation session.");
        ValidateCheckpoint(state);
        var restoredRandom = new SimulationRandomStreams(Random.Seed);
        restoredRandom.RestoreState(state.Random);
        var restoredCommands = new SimulationCommandQueue(limits);
        restoredCommands.RestoreState(state.Commands, state.Clock.TickIndex);
        capturing = true;
        try
        {
            Clock.RestoreState(state.Clock);
            world.RestoreState(state.World.Clone());
            Random = restoredRandom;
            commands = restoredCommands;
            completedTick = state.Clock.TickIndex;
            started = true;
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            capturing = false;
        }
    }

    /// <summary>Hashes the full checkpoint, including pending commands and random states but excluding frame backlog.</summary>
    public string ComputeStateHash() => SimulationStateHash.Compute(JsonSerializer.SerializeToElement(CaptureState()));

    /// <summary>Creates a RuntimeSave snapshot with the required simulation extension and default stream scalar.</summary>
    public RuntimeSaveSnapshot CaptureSave()
    {
        var state = CaptureState();
        return new RuntimeSaveSnapshot
        {
            Tick = state.Clock.TickIndex,
            RngState = state.Random.Streams.Single(entry => entry.Name == "default").State,
            State = JsonNode.Parse(state.World.GetRawText())!.AsObject(),
            Extensions = new Dictionary<string, RuntimeSaveExtension>(StringComparer.Ordinal)
            {
                [SimulationCheckpoint.ExtensionName] = new()
                {
                    Required = true,
                    Data = JsonSerializer.SerializeToNode(state)
                }
            }
        };
    }

    /// <summary>Restores a content-validated RuntimeSave snapshot and checks its mirrored tick, RNG and game state.</summary>
    public void RestoreSave(RuntimeSaveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = ReadCheckpoint(snapshot);
        ValidateCheckpoint(state);
        if (snapshot.Tick != state.Clock.TickIndex ||
            snapshot.RngState != state.Random.Streams.Single(entry => entry.Name == "default").State ||
            SimulationStateHash.Compute(JsonSerializer.SerializeToElement(snapshot.State)) !=
            SimulationStateHash.Compute(state.World))
            throw new InvalidDataException("Runtime save mirrors do not match its simulation checkpoint.");
        RestoreState(state);
    }

    void ValidateCheckpoint(SimulationCheckpoint state)
    {
        if (!HasCompatibleMetadata(state))
            throw new InvalidDataException("Simulation checkpoint metadata is incompatible.");
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = Clock.FixedDeltaTime });
        clock.RestoreState(state.Clock);
        if (state.Random.Streams is null || state.Random.Streams.Count(entry => entry?.Name == "default") != 1)
            throw new InvalidDataException("Simulation checkpoint requires one default random stream.");
    }

    bool HasCompatibleMetadata(SimulationCheckpoint state) =>
        state.Version == SimulationCheckpoint.CurrentVersion && state.Compatibility == Compatibility &&
        state.Clock is not null && state.Random is not null && state.Commands is not null &&
        state.World.ValueKind == JsonValueKind.Object;

    static SimulationCheckpoint ReadCheckpoint(RuntimeSaveSnapshot snapshot)
    {
        if (snapshot.Extensions is null ||
            !snapshot.Extensions.TryGetValue(SimulationCheckpoint.ExtensionName, out var extension) ||
            extension is null || !extension.Required || extension.Data is null)
            throw new InvalidDataException("The required simulation checkpoint is missing.");
        return extension.Data.Deserialize<SimulationCheckpoint>()
            ?? throw new InvalidDataException("The simulation checkpoint is empty.");
    }

    void EnsureAdmission()
    {
        if (faulted || Clock.IsFaulted || capturing)
            throw new InvalidOperationException("The simulation has faulted or is capturing/restoring state.");
        if (executingTick == 0) EnsureBoundary();
    }

    void EnsureBoundary()
    {
        if (faulted || Clock.IsFaulted || capturing || executingTick != 0 || Clock.TickIndex != completedTick)
            throw new InvalidOperationException("The simulation is outside a completed boundary or has faulted.");
    }

    void EnsureDrivingBoundary()
    {
        EnsureBoundary();
        if (publishing) throw new InvalidOperationException("The completed tick cannot reenter the simulation driver.");
    }
}
