namespace Turian.Engine.Core;

sealed class SimulationCommandQueue
{
    readonly SimulationCommandLimits limits;
    readonly List<SimulationCommand> pending = [];
    readonly Dictionary<Guid, long> producers = [];
    long lastSequence;

    internal SimulationCommandQueue(SimulationCommandLimits? limits)
    {
        this.limits = limits ?? new SimulationCommandLimits();
        if (this.limits.MaximumPending <= 0 || this.limits.MaximumProducers <= 0 ||
            this.limits.MaximumPayloadBytes <= 0 || this.limits.MaximumFutureTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Command limits must be positive.");
    }

    internal SimulationCommandAdmission Schedule(Guid producer, long sequence, string kind, int version,
        JsonElement payload, long targetTick, long boundary)
    {
        if (lastSequence == long.MaxValue)
            return new(SimulationCommandStatus.LimitExceeded, null);
        var command = new SimulationCommand
        {
            Producer = producer,
            ProducerSequence = sequence,
            Kind = kind,
            Version = version,
            Payload = payload,
            TargetTick = targetTick,
            AuthoritySequence = lastSequence + 1
        };
        return Accept(command, boundary);
    }

    internal SimulationCommandAdmission Accept(SimulationCommand command, long boundary)
    {
        var status = Check(command, boundary);
        if (status != SimulationCommandStatus.Accepted) return new(status, null);
        if (producers.TryGetValue(command.Producer, out var cursor) && command.ProducerSequence <= cursor)
            return new(SimulationCommandStatus.DuplicateOrStale, null);
        if (IsAtCapacity(command.Producer))
            return new(SimulationCommandStatus.LimitExceeded, null);
        if (lastSequence == long.MaxValue || command.AuthoritySequence != lastSequence + 1)
            return new(SimulationCommandStatus.Invalid, null);
        var owned = command with { Payload = command.Payload.Clone() };
        pending.Add(owned);
        producers[owned.Producer] = owned.ProducerSequence;
        lastSequence = owned.AuthoritySequence;
        return new(SimulationCommandStatus.Accepted, owned);
    }

    internal SimulationCommand[] Seal(long tick)
    {
        var batch = pending.Where(command => command.TargetTick == tick)
            .OrderBy(command => command.AuthoritySequence).ToArray();
        pending.RemoveAll(command => command.TargetTick == tick);
        return batch;
    }

    internal SimulationCommandsState CaptureState() => new()
    {
        LastAuthoritySequence = lastSequence,
        Pending = [.. pending.OrderBy(command => command.TargetTick).ThenBy(command => command.AuthoritySequence)],
        Producers = [.. producers.OrderBy(entry => entry.Key).Select(entry => new SimulationProducerState
        {
            Producer = entry.Key,
            Sequence = entry.Value
        })]
    };

    internal void RestoreState(SimulationCommandsState state, long boundary)
    {
        ValidateMetadata(state);
        var cursors = ReadCursors(state.Producers);
        var commands = ReadPending(state, cursors, boundary);
        foreach (var entry in cursors) producers.Add(entry.Key, entry.Value);
        pending.AddRange(commands);
        lastSequence = state.LastAuthoritySequence;
    }

    void ValidateMetadata(SimulationCommandsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (lastSequence != 0) throw new InvalidOperationException("Restore requires a fresh command queue.");
        if (state.LastAuthoritySequence < 0 || state.Pending is null || state.Producers is null ||
            state.Pending.Length > limits.MaximumPending || state.Producers.Length > limits.MaximumProducers ||
            state.Producers.LongLength > state.LastAuthoritySequence)
            throw new InvalidDataException("Command checkpoint metadata or limits are invalid.");
    }

    static Dictionary<Guid, long> ReadCursors(SimulationProducerState[] entries)
    {
        var cursors = new Dictionary<Guid, long>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.Producer == Guid.Empty || entry.Sequence <= 0 ||
                !cursors.TryAdd(entry.Producer, entry.Sequence))
                throw new InvalidDataException("Command producer cursors are invalid.");
        }
        return cursors;
    }

    SimulationCommand[] ReadPending(SimulationCommandsState state, Dictionary<Guid, long> cursors, long boundary)
    {
        var authoritySequences = new HashSet<long>();
        var requestIds = new HashSet<(Guid, long)>();
        var commands = new List<SimulationCommand>();
        foreach (var command in state.Pending)
        {
            ValidatePending(command, state.LastAuthoritySequence, cursors, boundary);
            if (!authoritySequences.Add(command.AuthoritySequence) ||
                !requestIds.Add((command.Producer, command.ProducerSequence)))
                throw new InvalidDataException("Command checkpoint contains duplicate identities.");
            commands.Add(command with { Payload = command.Payload.Clone() });
        }
        return [.. commands];
    }

    void ValidatePending(SimulationCommand command, long last, Dictionary<Guid, long> cursors, long boundary)
    {
        if (command is null || Check(command, boundary) != SimulationCommandStatus.Accepted ||
            command.AuthoritySequence > last || !cursors.TryGetValue(command.Producer, out var cursor) ||
            command.ProducerSequence > cursor)
            throw new InvalidDataException("A pending command is incompatible with its checkpoint.");
    }

    SimulationCommandStatus Check(SimulationCommand command, long boundary)
    {
        if (!HasValidIdentity(command) || !HasValidPayload(command))
            return SimulationCommandStatus.Invalid;
        if (command.TargetTick <= boundary || command.TargetTick - boundary > limits.MaximumFutureTicks)
            return SimulationCommandStatus.Invalid;
        return Encoding.UTF8.GetByteCount(command.Payload.GetRawText()) > limits.MaximumPayloadBytes
            ? SimulationCommandStatus.LimitExceeded : SimulationCommandStatus.Accepted;
    }

    bool IsAtCapacity(Guid producer) => pending.Count >= limits.MaximumPending ||
        (!producers.ContainsKey(producer) && producers.Count >= limits.MaximumProducers);

    static bool HasValidIdentity(SimulationCommand command) => command.Producer != Guid.Empty &&
        command.ProducerSequence > 0 && command.AuthoritySequence > 0;

    static bool HasValidPayload(SimulationCommand command) => !string.IsNullOrWhiteSpace(command.Kind) &&
        command.Kind.Length <= 128 && command.Version > 0 && command.Payload.ValueKind == JsonValueKind.Object;
}

/// <summary>Accepted pending commands and deduplication cursors at a completed tick.</summary>
public sealed record SimulationCommandsState
{
    /// <summary>The last assigned authority sequence, including commands already executed.</summary>
    [JsonRequired] public long LastAuthoritySequence { get; init; }

    /// <summary>The pending commands in canonical target-tick and authority-sequence order.</summary>
    [JsonRequired] public SimulationCommand[] Pending { get; init; } = [];

    /// <summary>The highest accepted request sequence of each producer.</summary>
    [JsonRequired] public SimulationProducerState[] Producers { get; init; } = [];
}

/// <summary>Deduplication state retained across save-resume and authenticated reconnection.</summary>
public sealed record SimulationProducerState
{
    /// <summary>The authority-established producer identity.</summary>
    [JsonRequired] public Guid Producer { get; init; }

    /// <summary>The highest accepted request sequence for this producer.</summary>
    [JsonRequired] public long Sequence { get; init; }
}
