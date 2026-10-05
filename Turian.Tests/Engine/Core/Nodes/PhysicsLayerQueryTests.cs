namespace Turian.Tests;

/// <summary>Checks physics layer filtering at the query service boundary used by physics backends.</summary>
public sealed class PhysicsLayerQueryTests
{
    /// <summary>All queries skip excluded and inactive shapes even when those shapes are nearer.</summary>
    [Fact]
    public void RaycastOverlapAndShapeCastSkipExcludedPhysicsLayers()
    {
        var excluded = new Node { PhysicsLayer = 4, RenderLayer = 0 };
        var included = new Node { PhysicsLayer = 2, RenderLayer = 4 };
        var inactive = new Node { PhysicsLayer = 2, IsActive = false };
        var backend = Substitute.For<IPhysicsQueryBackend>();
        var ray = new Ray(Vector3.Zero, Vector3.UnitZ);
        var bounds = new Bounds(Vector3.Zero, Vector3.One);
        PhysicsHit[] hits = [new(excluded, ray.GetPoint(1), Vector3.UnitZ, 1),
            new(inactive, ray.GetPoint(2), Vector3.UnitZ, 2), new(included, ray.GetPoint(3), Vector3.UnitZ, 3)];
        backend.RaycastAll(ray, 10).Returns(hits);
        backend.BoxCastAll(bounds, Vector3.UnitZ, 10).Returns(hits);
        backend.OverlapBox(bounds).Returns([excluded, inactive, included, included]);
        var queries = new PhysicsQueries(new LayerFilter(), backend);
        var mask = LayerMask.FromLayer(2);

        Assert.Equal(hits[2], queries.Raycast(ray, 10, mask));
        Assert.Equal([hits[2]], queries.RaycastAll(ray, 10, mask));
        Assert.Equal(hits[2], queries.BoxCast(bounds, Vector3.UnitZ, 10, mask));
        Assert.Equal([included], queries.OverlapBox(bounds, mask));
        Assert.Equal(hits[0], queries.Raycast(ray, 10));
        Assert.Null(queries.Raycast(ray, 10, LayerMask.Nothing));
        Assert.Null(queries.BoxCast(bounds, Vector3.UnitZ, 10, LayerMask.Nothing));
        Assert.Empty(queries.OverlapBox(bounds, LayerMask.Nothing));
    }

    /// <summary>The DI workflow registers query filtering without requiring a physics backend.</summary>
    [Fact]
    public void ServicesWithoutBackendReturnMisses()
    {
        using var services = new ServiceCollection().AddSingleton<IAppSettings>(new AppSettings())
            .AddInternalServices(typeof(Node).Assembly).BuildServiceProvider();
        var queries = services.GetRequiredService<IPhysicsQueries>();
        var ray = new Ray(Vector3.Zero, Vector3.UnitZ);
        var bounds = new Bounds(Vector3.Zero, Vector3.One);
        Assert.Null(queries.Raycast(ray));
        Assert.Empty(queries.RaycastAll(ray));
        Assert.Empty(queries.OverlapBox(bounds));
        Assert.Null(queries.BoxCast(bounds, Vector3.UnitZ));
        Assert.Same(services.GetRequiredService<LayerFilter>(), services.GetRequiredService<LayerFilter>());
    }
}
