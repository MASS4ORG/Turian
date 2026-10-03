namespace Turian.Engine.Core;

/// <summary>An authority-stamped command; identities and scheduling are assigned by trusted session code.</summary>
public sealed record SimulationCommand
{
    /// <summary>The tick at which this command is applied.</summary>
    [JsonRequired] public long TargetTick { get; init; }

    /// <summary>The authority's monotonically increasing admission sequence.</summary>
    [JsonRequired] public long AuthoritySequence { get; init; }

    /// <summary>The producer identity established by local ownership or an authenticated connection.</summary>
    [JsonRequired] public Guid Producer { get; init; }

    /// <summary>The producer's monotonically increasing request sequence; numbering starts at one.</summary>
    [JsonRequired] public long ProducerSequence { get; init; }

    /// <summary>The game-owned stable command kind.</summary>
    [JsonRequired] public required string Kind { get; init; }

    /// <summary>The command payload schema understood by the game.</summary>
    [JsonRequired] public int Version { get; init; } = 1;

    /// <summary>Owned JSON payload, independent of the caller's document lifetime.</summary>
    [JsonRequired] public JsonElement Payload { get; init; }
}

/// <summary>Whether a request entered the authoritative queue.</summary>
public enum SimulationCommandStatus
{
    /// <summary>The command was stamped and queued.</summary>
    Accepted,
    /// <summary>The producer sequence was already accepted or is older than its cursor.</summary>
    DuplicateOrStale,
    /// <summary>The request has invalid identity, schema, payload or scheduling metadata.</summary>
    Invalid,
    /// <summary>A configured queue, producer or payload limit was exceeded.</summary>
    LimitExceeded
}

/// <summary>Admission result; only accepted requests carry a command to record in the replay log.</summary>
/// <param name="Status">The outcome of admission.</param>
/// <param name="Command">The owned authority-stamped command, or null when rejected.</param>
public sealed record SimulationCommandAdmission(
    SimulationCommandStatus Status,
    SimulationCommand? Command);

/// <summary>The game validation outcome for a command applied at its tick boundary.</summary>
/// <param name="Command">The command whose execution was attempted.</param>
/// <param name="Applied">Whether the game applied the command; false represents a normal game-rule rejection.</param>
public sealed record SimulationCommandResult(
    SimulationCommand Command,
    bool Applied);

/// <summary>Copied admission limits bounding memory and future scheduling for one session.</summary>
public sealed record SimulationCommandLimits
{
    /// <summary>The maximum number of accepted commands waiting for execution.</summary>
    public int MaximumPending { get; init; } = 4096;

    /// <summary>The maximum number of tracked producers, including disconnected producer cursors.</summary>
    public int MaximumProducers { get; init; } = 256;

    /// <summary>The maximum UTF-8 payload size per command.</summary>
    public int MaximumPayloadBytes { get; init; } = 65536;

    /// <summary>The furthest target tick allowed ahead of the current boundary.</summary>
    public int MaximumFutureTicks { get; init; } = 3600;
}
