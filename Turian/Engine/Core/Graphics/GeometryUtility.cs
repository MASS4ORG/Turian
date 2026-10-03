namespace Turian.Engine.Core;

/// <summary>Camera frustum plane extraction and conservative axis-aligned bounds tests.</summary>
public static class GeometryUtility
{
    /// <summary>Returns inward-facing left, right, bottom, top, near and far planes for a camera.</summary>
    public static Plane[] CalculateFrustumPlanes(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var planes = new Plane[6];
        CalculateFrustumPlanes(camera.GetViewMatrix() * camera.GetProjectionMatrix(), planes);
        return planes;
    }

    /// <summary>Writes six world-space planes from a row-vector view-projection matrix with depth in [0, 1].</summary>
    public static void CalculateFrustumPlanes(Matrix4x4 matrix, Span<Plane> planes)
    {
        if (planes.Length < 6) throw new ArgumentException("Six plane slots are required.", nameof(planes));

        planes[0] = Normalize(new Plane(matrix.M14 + matrix.M11, matrix.M24 + matrix.M21,
            matrix.M34 + matrix.M31, matrix.M44 + matrix.M41));
        planes[1] = Normalize(new Plane(matrix.M14 - matrix.M11, matrix.M24 - matrix.M21,
            matrix.M34 - matrix.M31, matrix.M44 - matrix.M41));
        planes[2] = Normalize(new Plane(matrix.M14 + matrix.M12, matrix.M24 + matrix.M22,
            matrix.M34 + matrix.M32, matrix.M44 + matrix.M42));
        planes[3] = Normalize(new Plane(matrix.M14 - matrix.M12, matrix.M24 - matrix.M22,
            matrix.M34 - matrix.M32, matrix.M44 - matrix.M42));
        planes[4] = Normalize(new Plane(matrix.M13, matrix.M23, matrix.M33, matrix.M43));
        planes[5] = Normalize(new Plane(matrix.M14 - matrix.M13, matrix.M24 - matrix.M23,
            matrix.M34 - matrix.M33, matrix.M44 - matrix.M43));
    }

    /// <summary>Returns whether a box intersects every plane; unknown bounds stay visible.</summary>
    public static bool TestPlanesAABB(ReadOnlySpan<Plane> planes, Bounds bounds)
    {
        if (bounds.IsEmpty) return true;
        foreach (ref readonly var plane in planes)
        {
            var positive = new Vector3(
                plane.Normal.X >= 0f ? bounds.Max.X : bounds.Min.X,
                plane.Normal.Y >= 0f ? bounds.Max.Y : bounds.Min.Y,
                plane.Normal.Z >= 0f ? bounds.Max.Z : bounds.Min.Z);
            if (Plane.DotCoordinate(plane, positive) < -0.0001f) return false;
        }

        return true;
    }

    static Plane Normalize(Plane plane) =>
        plane.Normal.LengthSquared() > 0f ? Plane.Normalize(plane) : default;
}
