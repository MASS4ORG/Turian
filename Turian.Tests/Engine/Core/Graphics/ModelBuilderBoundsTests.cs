namespace Turian.Tests;

/// <summary>Verifies procedural models receive usable per-submesh bounds without overwriting imported bounds.</summary>
public sealed class ModelBuilderBoundsTests
{
    /// <summary>Indexed ranges compute separate bounds and preserve supplied bounds.</summary>
    [Fact]
    public void ResolvesSelectedIndexedRanges()
    {
        var supplied = new Bounds(new Vector3(-10f), new Vector3(10f));
        var builder = new ModelBuilder
        {
            Vertices = [new(new Vector3(1f, 2f, 3f), Vector3.One), new(new Vector3(4f, 5f, 6f), Vector3.One)],
            Indices = [1, 0],
            SubMeshes = [new SubMesh(0, 1), new SubMesh(1, 1, Bounds: Bounds.Empty),
                new SubMesh(0, 2, Bounds: supplied)],
        };
        var subs = builder.GetBoundedSubMeshes();
        Assert.Equal(new Bounds(new Vector3(4f, 5f, 6f), new Vector3(4f, 5f, 6f)), subs[0].Bounds);
        Assert.Equal(new Bounds(new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 3f)), subs[1].Bounds);
        Assert.Equal(supplied, subs[2].Bounds);
        Assert.Equal(default, builder.SubMeshes[0].Bounds);
    }

    /// <summary>Models without a submesh table use all vertices or indices as one bounded draw.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildsDefaultRange(bool indexed)
    {
        var builder = new ModelBuilder
        {
            Vertices = [new(new Vector3(-2f), Vector3.One), new(new Vector3(3f), Vector3.One)],
            Indices = indexed ? [1, 0] : [],
        };
        var sub = Assert.Single(builder.GetBoundedSubMeshes());
        Assert.Equal(2u, sub.IndexCount);
        Assert.Equal(new Bounds(new Vector3(-2f), new Vector3(3f)), sub.Bounds);
    }

    /// <summary>An empty procedural model has unknown bounds.</summary>
    [Fact]
    public void EmptyModelHasEmptyBounds()
    {
        Assert.Equal(Bounds.Empty, Assert.Single(new ModelBuilder().GetBoundedSubMeshes()).Bounds);
    }
}
