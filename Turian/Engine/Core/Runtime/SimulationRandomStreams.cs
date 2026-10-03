namespace Turian.Engine.Core;

/// <summary>Owns independently seeded named streams for one simulation; access belongs to its simulation thread.</summary>
public sealed class SimulationRandomStreams(ulong seed, int maximumStreams = 128)
{
    readonly SortedDictionary<string, SimulationRandom> streams = new(StringComparer.Ordinal);
    readonly int maximumStreams = maximumStreams > 0
        ? maximumStreams : throw new ArgumentOutOfRangeException(nameof(maximumStreams));

    /// <summary>The session seed used to derive streams, independent of their creation order.</summary>
    public ulong Seed { get; } = seed;

    /// <summary>Gets a named stream, deriving its seed from SHA-256 of the session seed and UTF-8 name.</summary>
    public SimulationRandom Get(string name)
    {
        ValidateName(name);
        if (streams.TryGetValue(name, out var existing)) return existing;
        if (streams.Count >= maximumStreams)
            throw new InvalidOperationException("The simulation random stream limit was reached.");
        var created = new SimulationRandom(DeriveSeed(Seed, name));
        streams.Add(name, created);
        return created;
    }

    /// <summary>Captures all generator states in ordinal name order.</summary>
    public SimulationRandomStreamsState CaptureState() => new()
    {
        Seed = Seed,
        Streams = [.. streams.Select(entry => new SimulationRandomStreamState
        {
            Name = entry.Key,
            Seed = entry.Value.Seed,
            State = entry.Value.State
        })]
    };

    /// <summary>Restores validated streams into a fresh collection without invalidating existing stream references.</summary>
    public void RestoreState(SimulationRandomStreamsState state)
    {
        if (streams.Count != 0) throw new InvalidOperationException("Restore requires fresh random streams.");
        var restored = ValidateState(state);
        foreach (var entry in restored) streams.Add(entry.Key, entry.Value);
    }

    SortedDictionary<string, SimulationRandom> ValidateState(SimulationRandomStreamsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != SimulationRandomStreamsState.CurrentVersion || state.Seed != Seed ||
            state.Streams is null || state.Streams.Length > maximumStreams)
            throw new InvalidDataException("Random stream metadata is incompatible.");
        var restored = new SortedDictionary<string, SimulationRandom>(StringComparer.Ordinal);
        foreach (var entry in state.Streams)
            RestoreEntry(restored, entry);
        return restored;
    }

    void RestoreEntry(SortedDictionary<string, SimulationRandom> restored, SimulationRandomStreamState entry)
    {
        if (entry is null || !HasValidName(entry.Name))
            throw new InvalidDataException("A random stream entry has a missing or invalid name.");
        if (entry.Seed != DeriveSeed(Seed, entry.Name) || restored.ContainsKey(entry.Name))
            throw new InvalidDataException("A random stream seed or name is invalid.");
        var random = new SimulationRandom(entry.Seed);
        random.Restore(entry.State);
        restored.Add(entry.Name, random);
    }

    static void ValidateName(string name)
    {
        if (!HasValidName(name))
            throw new ArgumentException("Stream names must be nonempty, at most 128 characters and contain no surrogates.",
                nameof(name));
    }

    static bool HasValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
        !name.Any(char.IsSurrogate);

    static ulong DeriveSeed(ulong seed, string name)
    {
        var bytes = new byte[8 + Encoding.UTF8.GetByteCount(name)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, seed);
        Encoding.UTF8.GetBytes(name, bytes.AsSpan(8));
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(bytes));
    }
}

/// <summary>Versioned session seed and named SplitMix64 generator states.</summary>
public sealed record SimulationRandomStreamsState
{
    /// <summary>The supported algorithm and state schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The SplitMix64 and SHA-256 seed derivation version.</summary>
    [JsonRequired] public int Version { get; init; } = CurrentVersion;

    /// <summary>The seed from which all named streams are derived.</summary>
    [JsonRequired] public ulong Seed { get; init; }

    /// <summary>The named streams and their complete state.</summary>
    [JsonRequired] public SimulationRandomStreamState[] Streams { get; init; } = [];
}

/// <summary>Serializable state of a single named random stream.</summary>
public sealed record SimulationRandomStreamState
{
    /// <summary>The stable ordinal stream name.</summary>
    [JsonRequired] public required string Name { get; init; }

    /// <summary>The derived creation seed.</summary>
    [JsonRequired] public ulong Seed { get; init; }

    /// <summary>The current SplitMix64 state.</summary>
    [JsonRequired] public ulong State { get; init; }
}
