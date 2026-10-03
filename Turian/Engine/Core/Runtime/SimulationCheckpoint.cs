namespace Turian.Engine.Core;

/// <summary>A completed boundary containing clock, commands, random streams and game-selected authoritative fields.</summary>
public sealed record SimulationCheckpoint
{
    /// <summary>The required RuntimeSave extension holding this checkpoint.</summary>
    public const string ExtensionName = "org.mass4.turian.simulation";

    /// <summary>The supported checkpoint schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The checkpoint schema version.</summary>
    [JsonRequired] public int Version { get; init; } = CurrentVersion;

    /// <summary>The game-defined build/content/system/command compatibility identifier.</summary>
    [JsonRequired] public required string Compatibility { get; init; }

    /// <summary>The completed simulation clock state.</summary>
    [JsonRequired] public required SimulationClockState Clock { get; init; }

    /// <summary>The seed and every authoritative random stream.</summary>
    [JsonRequired] public required SimulationRandomStreamsState Random { get; init; }

    /// <summary>The pending accepted commands and producer cursors.</summary>
    [JsonRequired] public required SimulationCommandsState Commands { get; init; }

    /// <summary>The authoritative state selected by the game, excluding presentation.</summary>
    [JsonRequired] public JsonElement World { get; init; }
}
