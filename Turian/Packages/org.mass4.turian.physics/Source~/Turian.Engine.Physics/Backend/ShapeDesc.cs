namespace Turian.Engine.Physics;

/// <summary>The kind of geometry a <see cref="ShapeDesc"/> describes.</summary>
public enum ShapeType
{
    /// <summary>A box centered on its pose.</summary>
    Box,

    /// <summary>A sphere centered on its pose.</summary>
    Sphere,

    /// <summary>A capsule along its local Y axis.</summary>
    Capsule,

    /// <summary>A cylinder along its local Y axis.</summary>
    Cylinder,

    /// <summary>The convex hull of a point cloud.</summary>
    ConvexHull,

    /// <summary>A one-sided triangle mesh; only static and kinematic bodies may use it.</summary>
    TriangleMesh,
}

/// <summary>
/// Collision geometry in world units. Sizes include the owning node's scale, since physics shapes have none.
/// </summary>
public readonly record struct ShapeDesc
{
    /// <summary>The kind of geometry.</summary>
    public ShapeType Type { get; init; }

    /// <summary>The full box extents.</summary>
    public Vector3 Size { get; init; }

    /// <summary>The sphere, capsule or cylinder radius.</summary>
    public float Radius { get; init; }

    /// <summary>The capsule's distance between its sphere centers, or the cylinder's full height.</summary>
    public float Length { get; init; }

    /// <summary>The hull's points or the mesh's vertices, in the shape's local space.</summary>
    public Vector3[]? Points { get; init; }

    /// <summary>The mesh's triangle vertex indices, three per triangle.</summary>
    public int[]? Indices { get; init; }

    /// <summary>A box with full extents <paramref name="size"/>.</summary>
    public static ShapeDesc Box(Vector3 size) => new() { Type = ShapeType.Box, Size = size };

    /// <summary>A sphere.</summary>
    public static ShapeDesc Sphere(float radius) => new() { Type = ShapeType.Sphere, Radius = radius };

    /// <summary>A Y-aligned capsule whose sphere centers are <paramref name="length"/> apart.</summary>
    public static ShapeDesc Capsule(float radius, float length) =>
        new() { Type = ShapeType.Capsule, Radius = radius, Length = length };

    /// <summary>A Y-aligned cylinder.</summary>
    public static ShapeDesc Cylinder(float radius, float height) =>
        new() { Type = ShapeType.Cylinder, Radius = radius, Length = height };

    /// <summary>The convex hull of <paramref name="points"/>.</summary>
    public static ShapeDesc ConvexHull(Vector3[] points) => new() { Type = ShapeType.ConvexHull, Points = points };

    /// <summary>A triangle mesh.</summary>
    public static ShapeDesc TriangleMesh(Vector3[] vertices, int[] indices) =>
        new() { Type = ShapeType.TriangleMesh, Points = vertices, Indices = indices };

    /// <summary>Whether the geometry is convex, so dynamic bodies, queries and compounds accept it.</summary>
    public bool IsConvex => Type != ShapeType.TriangleMesh;

    /// <summary>Approximate volume, used to share a body's mass among its colliders.</summary>
    public float Volume => Type switch
    {
        ShapeType.Box => MathF.Abs(Size.X * Size.Y * Size.Z),
        ShapeType.Sphere => 4f / 3f * MathF.PI * Radius * Radius * Radius,
        ShapeType.Capsule => MathF.PI * Radius * Radius * (Length + 4f / 3f * Radius),
        ShapeType.Cylinder => MathF.PI * Radius * Radius * Length,
        _ => BoundsVolume(Points),
    };

    static float BoundsVolume(Vector3[]? points)
    {
        if (points is not { Length: > 0 })
        {
            return 0f;
        }

        var bounds = Bounds.Empty;
        foreach (var point in points)
        {
            bounds = bounds.Encapsulate(point);
        }

        var size = bounds.Size;
        return MathF.Max(size.X * size.Y * size.Z, 1e-6f);
    }
}
