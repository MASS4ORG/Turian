namespace Turian.Tests;

/// <summary>Tests for AssetIdFactory deterministic id derivation.</summary>
public class AssetIdFactoryTests
{
    /// <summary>Same parent + slot must always produce the same id.</summary>
    [Fact]
    public void Derive_IsDeterministic()
    {
        var parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var a = AssetIdFactory.Derive(parent, "material:0");
        var b = AssetIdFactory.Derive(parent, "material:0");
        Assert.Equal(a, b);
    }

    /// <summary>Different slots produce different ids.</summary>
    [Fact]
    public void Derive_DifferentSlots_ProduceDifferentIds()
    {
        var parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.NotEqual(
            AssetIdFactory.Derive(parent, "material:0"),
            AssetIdFactory.Derive(parent, "material:1"));
        Assert.NotEqual(
            AssetIdFactory.Derive(parent, "material:0"),
            AssetIdFactory.Derive(parent, "image:0"));
    }

    /// <summary>Different parents produce different ids for the same slot.</summary>
    [Fact]
    public void Derive_DifferentParents_ProduceDifferentIds()
    {
        var p1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var p2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.NotEqual(
            AssetIdFactory.Derive(p1, "material:0"),
            AssetIdFactory.Derive(p2, "material:0"));
    }

    /// <summary>Empty slot must throw.</summary>
    [Fact]
    public void Derive_EmptySlot_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetIdFactory.Derive(Guid.NewGuid(), ""));
    }
}
