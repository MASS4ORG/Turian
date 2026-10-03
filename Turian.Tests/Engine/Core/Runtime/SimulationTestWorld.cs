namespace Turian.Tests;

/// <summary>A small integer-only world separating authoritative state from its scene presentation.</summary>
sealed class SimulationTestWorld : ISimulationWorld
{
    /// <summary>The authoritative balance.</summary>
    public long Balance { get; private set; }

    /// <summary>The ordered command application trace.</summary>
    public List<long> Applied { get; } = [];

    /// <summary>Optional intent generation inside a tick for boundary tests.</summary>
    public Action<long>? OnTick { get; set; }

    /// <summary>Optional state capture callback for boundary tests.</summary>
    public Action? OnCapture { get; set; }

    /// <summary>Whether the restore callback fails.</summary>
    public bool FailRestore { get; set; }

    /// <inheritdoc/>
    public bool ApplyCommand(SimulationCommand command, SimulationRandomStreams random)
    {
        if (command.Kind != "add" || command.Version != 1) return false;
        Balance = checked(Balance + command.Payload.GetProperty("amount").GetInt64());
        Balance += random.Get("commands").NextInt(10);
        Applied.Add(command.AuthoritySequence);
        return true;
    }

    /// <inheritdoc/>
    public void Tick(long tick, double fixedDeltaTime, SimulationRandomStreams random)
    {
        OnTick?.Invoke(tick);
        Balance += random.Get("economy").NextInt(20);
    }

    /// <inheritdoc/>
    public JsonElement CaptureState()
    {
        OnCapture?.Invoke();
        return JsonSerializer.SerializeToElement(new { Balance, Applied });
    }

    /// <inheritdoc/>
    public void RestoreState(JsonElement state)
    {
        if (FailRestore) throw new InvalidOperationException("Invalid game state");
        var balance = state.GetProperty(nameof(Balance)).GetInt64();
        var applied = state.GetProperty(nameof(Applied)).EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Balance = balance;
        Applied.AddRange(applied);
    }
}
