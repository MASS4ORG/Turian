namespace Turian.Tests;

/// <summary>Checks all 32 layer bits, conversions, operators and serialized mask values.</summary>
public sealed class LayerMaskTests
{
    /// <summary>Bit operations and signed conversions preserve slot 31 without accepting invalid indices.</summary>
    [Fact]
    public void OperatorsAndConversionsCoverAllBits()
    {
        LayerMask low = 1;
        LayerMask high = 0x80000000u;
        var both = low | high;
        Assert.True(both.Contains(0));
        Assert.True(both.Contains(31));
        Assert.False(both.Contains(-1));
        Assert.False(both.Contains(32));
        Assert.Equal(high, both & high);
        Assert.True(both.Intersects(high));
        Assert.False(low.Intersects(high));
        Assert.Equal(LayerMask.Everything, both | ~both);
        Assert.Equal(LayerMask.Nothing, both & ~both);
        Assert.Equal(0x80000001u, (uint)both);
        Assert.Equal(unchecked((int)0x80000001), (int)both);
        Assert.Equal(LayerMask.Everything, (LayerMask)(-1));
        Assert.Equal(high, LayerMask.FromLayer(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerMask.FromLayer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerMask.FromLayer(32));
        Assert.Equal(both, JsonSerializer.Deserialize<LayerMask>(JsonSerializer.Serialize(both)));
        Assert.Equal(LayerMask.Everything, JsonSerializer.Deserialize<LayerMask>("-1"));
    }
}
