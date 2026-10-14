namespace Turian.Engine.Physics;

/// <summary>A ray from <see cref="Start"/> to <see cref="End"/>; hits report their fraction of that segment.</summary>
/// <param name="Start">The world start point.</param>
/// <param name="End">The world end point.</param>
/// <param name="Filter">The query's <see cref="CollisionFilter.CollidesWith"/> selects the colliders it accepts.</param>
/// <param name="HitTriggers">Whether trigger colliders are reported.</param>
public readonly record struct RaycastInput(Vector3 Start, Vector3 End, CollisionFilter Filter, bool HitTriggers = false);

/// <summary>A convex shape swept from <see cref="Start"/> to <see cref="End"/> at a fixed orientation.</summary>
/// <param name="Shape">The convex shape to sweep.</param>
/// <param name="Start">The world start position of the shape's center.</param>
/// <param name="Orientation">The shape's world orientation.</param>
/// <param name="End">The world end position of the shape's center.</param>
/// <param name="Filter">The query's <see cref="CollisionFilter.CollidesWith"/> selects the colliders it accepts.</param>
/// <param name="HitTriggers">Whether trigger colliders are reported.</param>
public readonly record struct ShapeCastInput(
    ShapeDesc Shape,
    Vector3 Start,
    Quaternion Orientation,
    Vector3 End,
    CollisionFilter Filter,
    bool HitTriggers = false);

/// <summary>A convex shape placed in the world to find what it touches.</summary>
/// <param name="Shape">The convex shape to test.</param>
/// <param name="Pose">The shape's world pose.</param>
/// <param name="Filter">The query's <see cref="CollisionFilter.CollidesWith"/> selects the colliders it accepts.</param>
/// <param name="HitTriggers">Whether trigger colliders are reported.</param>
public readonly record struct OverlapInput(ShapeDesc Shape, PhysicsPose Pose, CollisionFilter Filter,
    bool HitTriggers = false);

/// <summary>A collider found by a query.</summary>
/// <param name="Body">The body owning the collider.</param>
/// <param name="ColliderData">The collider's <see cref="ColliderDesc.UserData"/>.</param>
/// <param name="Point">The world hit point; overlaps report the collider's body position.</param>
/// <param name="Normal">The world surface normal facing the query; zero for overlaps.</param>
/// <param name="Fraction">The fraction of the cast travelled before the hit; zero when it starts overlapping.</param>
public readonly record struct PhysicsQueryHit(
    BodyHandle Body,
    ulong ColliderData,
    Vector3 Point,
    Vector3 Normal,
    float Fraction);

/// <summary>Receives hits as a query finds them, as Unity.Physics collectors do.</summary>
public interface IHitCollector
{
    /// <summary>Stops the query after the first accepted hit.</summary>
    bool EarlyOutOnFirstHit { get; }

    /// <summary>Hits beyond this fraction are not reported.</summary>
    float MaxFraction { get; }

    /// <summary>Takes a hit; returns whether it was kept.</summary>
    bool AddHit(in PhysicsQueryHit hit);
}

/// <summary>Keeps the nearest hit.</summary>
public struct ClosestHitCollector(float maxFraction) : IHitCollector
{
    /// <summary>Keeps the nearest hit along the whole query.</summary>
    public ClosestHitCollector() : this(1f)
    {
    }

    /// <summary>Whether a hit was found.</summary>
    public bool HasHit { get; private set; }

    /// <summary>The nearest hit so far.</summary>
    public PhysicsQueryHit Hit { get; private set; }

    /// <inheritdoc />
    public readonly bool EarlyOutOnFirstHit => false;

    /// <inheritdoc />
    public float MaxFraction { get; private set; } = maxFraction;

    /// <inheritdoc />
    public bool AddHit(in PhysicsQueryHit hit)
    {
        if (hit.Fraction > MaxFraction)
        {
            return false;
        }

        MaxFraction = hit.Fraction;
        Hit = hit;
        HasHit = true;
        return true;
    }
}

/// <summary>Keeps every hit in arrival order.</summary>
public sealed class AllHitsCollector(float maxFraction = 1f) : IHitCollector
{
    /// <summary>The hits found.</summary>
    public List<PhysicsQueryHit> Hits { get; } = [];

    /// <inheritdoc />
    public bool EarlyOutOnFirstHit => false;

    /// <inheritdoc />
    public float MaxFraction { get; } = maxFraction;

    /// <inheritdoc />
    public bool AddHit(in PhysicsQueryHit hit)
    {
        if (hit.Fraction > MaxFraction)
        {
            return false;
        }

        Hits.Add(hit);
        return true;
    }
}

/// <summary>Stops at the first hit, for yes-or-no checks.</summary>
public struct AnyHitCollector : IHitCollector
{
    /// <summary>Whether anything was hit.</summary>
    public bool HasHit { get; private set; }

    /// <inheritdoc />
    public readonly bool EarlyOutOnFirstHit => true;

    /// <inheritdoc />
    public readonly float MaxFraction => 1f;

    /// <inheritdoc />
    public bool AddHit(in PhysicsQueryHit hit)
    {
        HasHit = true;
        return true;
    }
}

/// <summary>The closest features of two shapes.</summary>
/// <param name="Distance">The gap between the shapes; negative when they penetrate.</param>
/// <param name="Normal">The world direction from the second shape towards the first.</param>
/// <param name="PointOnB">The closest point on the second shape's surface.</param>
public readonly record struct DistanceResult(float Distance, Vector3 Normal, Vector3 PointOnB);
