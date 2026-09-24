namespace Turian.Tests;

/// <summary>
/// Verifies <see cref="DataAsset.Instantiate"/> and <see cref="DataAssetPolicyAttribute"/> resolution.
/// See <see cref="RuntimeAssetLoaderDataTests"/> for the shared-instance caching these build on.
/// </summary>
public sealed class DataAssetTests
{
    /// <summary>A clone is a different object with the same field values.</summary>
    [Fact]
    public void InstantiateCopiesFieldValues()
    {
        var source = new DataAssetTest { Int = 42, Bool = false };

        var clone = source.Instantiate();

        Assert.NotSame(source, clone);
        Assert.IsType<DataAssetTest>(clone);
        Assert.Equal(42, ((DataAssetTest)clone).Int);
        Assert.False(((DataAssetTest)clone).Bool);
    }

    /// <summary>A clone gets its own id, so it is addressable as an independent instance.</summary>
    [Fact]
    public void InstantiateAssignsANewId()
    {
        var source = new DataAssetTest();

        var clone = source.Instantiate();

        Assert.NotEqual(source.Id, clone.Id);
    }

    /// <summary>Mutating the clone never touches the source — the whole point of per-instance data.</summary>
    [Fact]
    public void InstantiateIsIndependentOfTheSource()
    {
        var source = new DataAssetTest { Int = 1 };

        var clone = (DataAssetTest)source.Instantiate();
        clone.Int = 2;

        Assert.Equal(1, source.Int);
        Assert.Equal(2, clone.Int);
    }

    /// <summary>A type with no <see cref="DataAssetPolicyAttribute"/> defaults to <see cref="DataAssetPolicy.Authored"/>.</summary>
    [Fact]
    public void PolicyDefaultsToAuthored() =>
        Assert.Equal(DataAssetPolicy.Authored, DataAssetPolicyAttribute.Resolve(typeof(DataAssetTest)));

    /// <summary>A type's declared policy is what <see cref="DataAssetPolicyAttribute.Resolve"/> returns.</summary>
    [Fact]
    public void PolicyResolvesTheDeclaredAttribute() =>
        Assert.Equal(DataAssetPolicy.Persistent, DataAssetPolicyAttribute.Resolve(typeof(PersistentTestData)));

    [DataAssetPolicy(DataAssetPolicy.Persistent)]
    [TypeId("c0000001-0000-4000-8000-000000000001")]
    sealed class PersistentTestData : DataAsset;
}
