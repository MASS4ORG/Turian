namespace Turian.Tests;

/// <summary>Checks timestamp decoding without creating a Vulkan device.</summary>
public sealed class GpuTimestampQueriesTests
{
    /// <summary>Converts the four timestamps into three sequential phase timings.</summary>
    [Fact]
    public void DecodesPhaseOffsetsAndDurations()
    {
        Assert.True(GpuTimestampQueries.TryDecode([10, 12, 15, 19], 64, 1_000_000,
            TimeSpan.FromSeconds(1), out var passes));

        Assert.Equal(3, passes.Length);
        Assert.Equal(new GpuPassTiming("GPU preparation", 0, 2), passes[0]);
        Assert.Equal(new GpuPassTiming("Scene render pass", 2, 3), passes[1]);
        Assert.Equal(new GpuPassTiming("Readback copy", 5, 4), passes[2]);
    }

    /// <summary>Handles a counter wrapping at its advertised width.</summary>
    [Theory]
    [InlineData(36)]
    [InlineData(64)]
    public void DecodesCounterWrap(uint bits)
    {
        var maximum = ulong.MaxValue >> (64 - (int)bits);
        Assert.True(GpuTimestampQueries.TryDecode([maximum - 1, 1, 3, 4], bits, 1_000_000,
            TimeSpan.FromSeconds(1), out var passes));

        Assert.Equal(3, passes[0].DurationMilliseconds);
        Assert.Equal(3, passes[1].StartMilliseconds);
        Assert.Equal(2, passes[1].DurationMilliseconds);
        Assert.Equal(5, passes[2].StartMilliseconds);
        Assert.Equal(1, passes[2].DurationMilliseconds);
    }

    /// <summary>A phase can have no elapsed device ticks.</summary>
    [Fact]
    public void DecodesZeroDurationPhase()
    {
        Assert.True(GpuTimestampQueries.TryDecode([10, 11, 11, 13], 64, 1_000_000,
            TimeSpan.FromSeconds(1), out var passes));

        Assert.Equal(0, passes[1].DurationMilliseconds);
        Assert.Equal(1, passes[2].StartMilliseconds);
    }

    /// <summary>Rejects unsupported counter widths and invalid timestamp periods.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(65, 1)]
    [InlineData(64, 0)]
    [InlineData(64, -1)]
    [InlineData(64, double.NaN)]
    [InlineData(64, double.PositiveInfinity)]
    [InlineData(64, double.MaxValue)]
    public void RejectsInvalidParameters(uint bits, double period)
    {
        Assert.False(GpuTimestampQueries.TryDecode([0, 1, 2, 3], bits, period,
            TimeSpan.FromMilliseconds(1), out var passes));
        Assert.Empty(passes);
    }

    /// <summary>Rejects an envelope long enough to hide a complete counter wrap.</summary>
    [Fact]
    public void RejectsAmbiguousHostEnvelope()
    {
        Assert.False(GpuTimestampQueries.TryDecode([0, 1, 2, 3], 8, 1_000,
            TimeSpan.FromMilliseconds(1), out var passes));
        Assert.Empty(passes);
    }

    /// <summary>A backwards boundary cannot represent a wrap within the submission envelope.</summary>
    [Fact]
    public void RejectsBackwardsBoundaryOutsideEnvelope()
    {
        Assert.False(GpuTimestampQueries.TryDecode([100, 101, 100, 102], 36, 1_000,
            TimeSpan.FromMilliseconds(1), out var passes));
        Assert.Empty(passes);
    }
}
