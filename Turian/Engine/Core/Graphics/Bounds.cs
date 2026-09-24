namespace Turian.Engine.Core;

/// <summary>
/// An axis-aligned bounding box in the space of whatever owns it.
/// </summary>
/// <param name="Min">Lowest corner.</param>
/// <param name="Max">Highest corner.</param>
public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    /// <summary>
    /// A box with an inverted range, so the first <see cref="Encapsulate(Vector3)"/>
    /// call collapses it onto that point.
    /// </summary>
    public static Bounds Empty { get; } = new(
        new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
        new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity));

    /// <summary>Gets a value indicating whether the box covers no volume because no point was added.</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>Gets the midpoint between <see cref="Min"/> and <see cref="Max"/>.</summary>
    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Gets the extent of the box along each axis.</summary>
    public Vector3 Size => Max - Min;

    /// <summary>Returns a box grown to contain <paramref name="point"/>.</summary>
    public Bounds Encapsulate(Vector3 point) => new(
        new Vector3(MathF.Min(Min.X, point.X), MathF.Min(Min.Y, point.Y), MathF.Min(Min.Z, point.Z)),
        new Vector3(MathF.Max(Max.X, point.X), MathF.Max(Max.Y, point.Y), MathF.Max(Max.Z, point.Z)));

    /// <summary>Returns a box grown to contain <paramref name="other"/>.</summary>
    public Bounds Encapsulate(Bounds other) =>
        other.IsEmpty ? this : Encapsulate(other.Min).Encapsulate(other.Max);

    /// <summary>
    /// Tests <paramref name="ray"/> against this box using the standard slab method.
    /// </summary>
    /// <param name="ray">The ray to test, in the same space as this box.</param>
    /// <param name="distance">
    /// The distance from the ray's origin to the nearest intersection, or <c>0</c> when the origin
    /// is already inside the box. Undefined when this method returns <c>false</c>.
    /// </param>
    /// <returns><c>true</c> when the ray hits the box at or ahead of its origin.</returns>
    public bool TryIntersect(Ray ray, out float distance)
    {
        distance = 0f;
        if (IsEmpty) return false;

        var tMin = 0f;
        var tMax = float.PositiveInfinity;

        for (var axis = 0; axis < 3; axis++)
        {
            var origin = axis switch { 0 => ray.Origin.X, 1 => ray.Origin.Y, _ => ray.Origin.Z };
            var direction = axis switch { 0 => ray.Direction.X, 1 => ray.Direction.Y, _ => ray.Direction.Z };
            var min = axis switch { 0 => Min.X, 1 => Min.Y, _ => Min.Z };
            var max = axis switch { 0 => Max.X, 1 => Max.Y, _ => Max.Z };

            if (MathF.Abs(direction) < 1e-9f)
            {
                // Parallel to this axis's slab: a miss unless the origin already lies within it.
                if (origin < min || origin > max) return false;
                continue;
            }

            var inv = 1f / direction;
            var t1 = (min - origin) * inv;
            var t2 = (max - origin) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        distance = tMin;
        return true;
    }
}
