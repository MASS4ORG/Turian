namespace Turian.Engine.Core;

/// <summary>Game-owned authoritative state driven on one simulation thread, independent of scene objects.</summary>
public interface ISimulationWorld
{
    /// <summary>Validates and applies an ordered command; false rejects it without mutating authoritative state.</summary>
    bool ApplyCommand(SimulationCommand command, SimulationRandomStreams random);

    /// <summary>Runs game systems in their explicit stable order for this fixed tick.</summary>
    void Tick(long tick, double fixedDeltaTime, SimulationRandomStreams random);

    /// <summary>Captures authoritative game fields as JSON; presentation state must be excluded.</summary>
    JsonElement CaptureState();

    /// <summary>Validates and restores a saved game state into a fresh world.</summary>
    void RestoreState(JsonElement state);
}
