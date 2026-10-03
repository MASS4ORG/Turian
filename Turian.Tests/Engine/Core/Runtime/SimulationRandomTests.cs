namespace Turian.Tests;

/// <summary>Checks algorithm vectors, named stream isolation and complete RNG state serialization.</summary>
public sealed class SimulationRandomTests
{
    /// <summary>The public-domain SplitMix64 reference sequence fixes the algorithm contract.</summary>
    [Fact]
    public void SplitMix64_MatchesReferenceVectorsAndWraps()
    {
        var random = new SimulationRandom(0);
        Assert.Equal(0xe220a8397b1dcdafUL, random.NextUInt64());
        Assert.Equal(0x6e789e6aa1b965f4UL, random.NextUInt64());
        Assert.Equal(0x06c45d188009454fUL, random.NextUInt64());
        var wrapping = new SimulationRandom(ulong.MaxValue);
        wrapping.NextUInt64();
        Assert.Equal(unchecked(ulong.MaxValue + 0x9e3779b97f4a7c15UL), wrapping.State);
        Assert.Equal(0UL, random.Seed);
        for (var index = 0; index < 200; index++)
        {
            Assert.InRange(random.NextInt(7), 0, 6);
            Assert.InRange(random.NextDouble(), 0, Math.BitDecrement(1d));
        }
        Assert.Equal(0, random.NextInt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(-1));
    }

    /// <summary>Creation order and extra draws in one subsystem do not alter another subsystem's sequence.</summary>
    [Fact]
    public void NamedStreams_AreIndependentAndResumeFromJson()
    {
        var first = new SimulationRandomStreams(42);
        var economy = first.Get("economy");
        var combat = first.Get("combat");
        Assert.Same(combat, first.Get("combat"));
        var second = new SimulationRandomStreams(42);
        var otherCombat = second.Get("combat");
        Assert.Equal(economy.NextUInt64(), second.Get("economy").NextUInt64());
        for (var index = 0; index < 20; index++) economy.NextUInt64();
        Assert.Equal(combat.NextUInt64(), otherCombat.NextUInt64());
        var state = first.CaptureState();
        Assert.Equal(new[] { "combat", "economy" }, state.Streams.Select(entry => entry.Name));
        var restored = new SimulationRandomStreams(42);
        restored.RestoreState(JsonSerializer.Deserialize<SimulationRandomStreamsState>(JsonSerializer.Serialize(state))!);
        Assert.Equal(economy.NextUInt64(), restored.Get("economy").NextUInt64());
        Assert.Equal(combat.NextUInt64(), restored.Get("combat").NextUInt64());
        Assert.Throws<InvalidOperationException>(() => restored.RestoreState(state));
    }

    /// <summary>Invalid versions, seeds, duplicate names and resource limits fail without populating the collection.</summary>
    [Fact]
    public void Restore_RejectsInvalidStatesBeforeMutation()
    {
        var original = new SimulationRandomStreams(123);
        original.Get("world");
        var state = original.CaptureState();
        var fresh = new SimulationRandomStreams(123);
        Assert.Throws<ArgumentNullException>(() => fresh.RestoreState(null!));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with { Seed = 12 }));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with { Streams = null! }));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with { Streams = [null!] }));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with
        { Streams = [state.Streams[0], state.Streams[0]] }));
        Assert.Throws<InvalidDataException>(() => fresh.RestoreState(state with
        { Streams = [state.Streams[0] with { Seed = 0 }] }));
        Assert.Empty(fresh.CaptureState().Streams);
        fresh.RestoreState(state);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationRandomStreams(1, 0));
        var limited = new SimulationRandomStreams(1, 1);
        limited.Get("a");
        Assert.Throws<InvalidOperationException>(() => limited.Get("b"));
        Assert.Throws<InvalidDataException>(() => new SimulationRandomStreams(123, 1).RestoreState(state with
        { Streams = [state.Streams[0], state.Streams[0]] }));
        Assert.Throws<ArgumentException>(() => fresh.Get(" "));
        Assert.Throws<ArgumentException>(() => fresh.Get(new string('x', 129)));
        Assert.Throws<ArgumentException>(() => fresh.Get("\ud800"));
    }
}
