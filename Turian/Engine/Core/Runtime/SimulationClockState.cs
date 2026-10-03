namespace Turian.Engine.Core;

/// <summary>Serializable clock state at a completed simulation boundary, excluding render-time backlog.</summary>
public sealed record SimulationClockState
{
    /// <summary>The clock state schema supported by this implementation.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The version of this clock state representation.</summary>
    [JsonRequired]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The last successfully completed simulation tick; zero identifies the initial state.</summary>
    [JsonRequired]
    public long TickIndex { get; init; }

    /// <summary>The fixed interval used by the saved simulation, in seconds.</summary>
    [JsonRequired]
    public double FixedDeltaTime { get; init; }

    /// <summary>The simulation speed applied to incoming unscaled time.</summary>
    [JsonRequired]
    public double TimeScale { get; init; }

    /// <summary>Whether the driver is paused independently of its configured speed.</summary>
    [JsonRequired]
    public bool IsPaused { get; init; }
}
