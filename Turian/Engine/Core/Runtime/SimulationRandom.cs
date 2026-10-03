namespace Turian.Engine.Core;

/// <summary>Versioned SplitMix64 stream for reproducible simulation randomness, independent of System.Random.</summary>
public sealed class SimulationRandom(ulong seed)
{
    /// <summary>The initial seed from which this stream was created.</summary>
    public ulong Seed { get; } = seed;

    /// <summary>The complete generator state to persist at a completed tick.</summary>
    public ulong State { get; private set; } = seed;

    /// <summary>Returns the next 64 random bits; arithmetic wraps modulo 2^64.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            State += 0x9e3779b97f4a7c15UL;
            var value = State;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>Returns an unbiased integer in [0, exclusiveMaximum), using rejection sampling.</summary>
    public int NextInt(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        var bound = (ulong)exclusiveMaximum;
        var threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do value = NextUInt64(); while (value < threshold);
        return (int)(value % bound);
    }

    /// <summary>Returns a uniformly distributed value in [0, 1) using 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1d / (1UL << 53));

    internal void Restore(ulong state) => State = state;
}
