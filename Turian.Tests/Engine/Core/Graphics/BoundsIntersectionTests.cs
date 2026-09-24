namespace Turian.Tests;

/// <summary>Tests for <see cref="Bounds.TryIntersect"/>, the slab test viewport picking relies on.</summary>
public class BoundsIntersectionTests
{
    static readonly Bounds unit = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    /// <summary>A ray aimed straight at the box hits it at the near face.</summary>
    [Fact]
    public void TryIntersect_RayThroughTheBox_Hits()
    {
        var ray = new Ray(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 1f));

        var hit = unit.TryIntersect(ray, out var distance);

        Assert.True(hit);
        Assert.Equal(4f, distance, 4);
    }

    /// <summary>A ray that passes beside the box misses.</summary>
    [Fact]
    public void TryIntersect_RayBesideTheBox_Misses()
    {
        var ray = new Ray(new Vector3(5f, 0f, -5f), new Vector3(0f, 0f, 1f));

        Assert.False(unit.TryIntersect(ray, out _));
    }

    /// <summary>A ray pointing away from the box misses, even though its infinite line would hit.</summary>
    [Fact]
    public void TryIntersect_RayPointingAway_Misses()
    {
        var ray = new Ray(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, -1f));

        Assert.False(unit.TryIntersect(ray, out _));
    }

    /// <summary>An origin already inside the box hits at distance zero.</summary>
    [Fact]
    public void TryIntersect_OriginInsideTheBox_HitsAtZero()
    {
        var ray = new Ray(Vector3.Zero, new Vector3(1f, 0f, 0f));

        var hit = unit.TryIntersect(ray, out var distance);

        Assert.True(hit);
        Assert.Equal(0f, distance);
    }

    /// <summary>An empty box never intersects.</summary>
    [Fact]
    public void TryIntersect_EmptyBox_AlwaysMisses()
    {
        var ray = new Ray(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 1f));

        Assert.False(Bounds.Empty.TryIntersect(ray, out _));
    }

    /// <summary>A ray parallel to an axis still resolves correctly through the other two slabs.</summary>
    [Fact]
    public void TryIntersect_AxisAlignedRay_Hits()
    {
        var ray = new Ray(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f));

        var hit = unit.TryIntersect(ray, out var distance);

        Assert.True(hit);
        Assert.Equal(4f, distance, 4);
    }
}
