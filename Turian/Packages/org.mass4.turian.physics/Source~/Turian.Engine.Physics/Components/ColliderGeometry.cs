namespace Turian.Engine.Physics;

/// <summary>Backend-free geometry of collider shapes: bounds and closest points.</summary>
static class ColliderGeometry
{
    /// <summary>The half extents of a shape in its own frame.</summary>
    public static Vector3 HalfExtents(in ShapeDesc shape) => shape.Type switch
    {
        ShapeType.Box => shape.Size * 0.5f,
        ShapeType.Sphere => new Vector3(shape.Radius),
        ShapeType.Capsule => new Vector3(shape.Radius, shape.Length * 0.5f + shape.Radius, shape.Radius),
        ShapeType.Cylinder => new Vector3(shape.Radius, shape.Length * 0.5f, shape.Radius),
        _ => PointsHalfExtents(shape.Points),
    };

    /// <summary>The tight world-space bounds of a collider.</summary>
    public static Bounds WorldBounds(ColliderComponent collider)
    {
        var pose = collider.WorldPose(out var shape);
        var center = pose.Position;
        var axis = Vector3.Transform(Vector3.UnitY, pose.Orientation);
        switch (shape.Type)
        {
            case ShapeType.Sphere:
                return new Bounds(center - new Vector3(shape.Radius), center + new Vector3(shape.Radius));
            case ShapeType.Capsule:
                var tip = Vector3.Abs(axis * shape.Length * 0.5f) + new Vector3(shape.Radius);
                return new Bounds(center - tip, center + tip);
            case ShapeType.Cylinder:
                // A disc spans its radius times the sine of each axis' angle to the cylinder's axis.
                var disc = shape.Radius * Vector3.SquareRoot(Vector3.Max(Vector3.Zero, Vector3.One - axis * axis));
                var reach = Vector3.Abs(axis * shape.Length * 0.5f) + disc;
                return new Bounds(center - reach, center + reach);
            case ShapeType.ConvexHull or ShapeType.TriangleMesh:
                return shape.Points is { Length: > 0 } points
                    ? points.Aggregate(Bounds.Empty, (box, point) => box.Encapsulate(pose.TransformPoint(point)))
                    : new Bounds(center, center);
        }

        var half = HalfExtents(shape);
        var rotation = Matrix4x4.CreateFromQuaternion(pose.Orientation);
        var extent = new Vector3(
            MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y + MathF.Abs(rotation.M31) * half.Z,
            MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y + MathF.Abs(rotation.M32) * half.Z,
            MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y + MathF.Abs(rotation.M33) * half.Z);
        return new Bounds(center - extent, center + extent);
    }

    /// <summary>The point on a collider closest to a world position; positions inside are returned unchanged.</summary>
    public static Vector3 ClosestPoint(ColliderComponent collider, Vector3 position)
    {
        var pose = collider.WorldPose(out var shape);
        var local = pose.InverseTransformPoint(position);
        var closest = shape.Type switch
        {
            ShapeType.Box => Vector3.Clamp(local, -shape.Size * 0.5f, shape.Size * 0.5f),
            ShapeType.Sphere => OnSphere(local, Vector3.Zero, shape.Radius),
            ShapeType.Capsule => OnSphere(local,
                new Vector3(0, Math.Clamp(local.Y, -shape.Length * 0.5f, shape.Length * 0.5f), 0), shape.Radius),
            ShapeType.Cylinder => OnCylinder(local, shape.Radius, shape.Length * 0.5f),
            _ => Vector3.Clamp(local, PointsBounds(shape.Points).Min, PointsBounds(shape.Points).Max),
        };
        return pose.TransformPoint(closest);
    }

    static Vector3 OnSphere(Vector3 point, Vector3 center, float radius)
    {
        var offset = point - center;
        var length = offset.Length();
        return length <= radius ? point : center + offset * (radius / length);
    }

    static Vector3 OnCylinder(Vector3 point, float radius, float halfHeight)
    {
        var radial = new Vector2(point.X, point.Z);
        var length = radial.Length();
        if (length > radius)
        {
            radial *= radius / length;
        }

        return new Vector3(radial.X, Math.Clamp(point.Y, -halfHeight, halfHeight), radial.Y);
    }

    static Vector3 PointsHalfExtents(Vector3[]? points) => PointsBounds(points).Size * 0.5f;

    static Bounds PointsBounds(Vector3[]? points)
    {
        if (points is not { Length: > 0 })
        {
            return new Bounds(Vector3.Zero, Vector3.Zero);
        }

        var bounds = Bounds.Empty;
        foreach (var point in points)
        {
            bounds = bounds.Encapsulate(point);
        }

        return bounds;
    }
}
