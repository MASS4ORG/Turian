namespace Turian.Engine.Physics;

public sealed partial class PhysicsScene : IPhysicsQueryBackend
{
    // Unbounded queries stop here, keeping hit fractions precise.
    const float MaxQueryDistance = 100_000f;

    /// <summary>Whether a ray hits any collider.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity,
        LayerMask? layerMask = null, QueryTriggerInteraction queryTriggerInteraction = default) =>
        Raycast(origin, direction, out _, maxDistance, layerMask, queryTriggerInteraction);

    /// <summary>Finds the nearest collider along a ray; colliders containing the origin are not hit.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default)
    {
        hit = default;
        if (World is null || !TryDirection(direction, maxDistance, out var unit, out var length))
        {
            return false;
        }

        var collector = new NearestHitCollector();
        World.CastRay(new RaycastInput(origin, origin + unit * length, FilterOf(layerMask),
            HitsTriggers(queryTriggerInteraction)), ref collector);
        return ToHit(collector, length, out hit);
    }

    /// <summary>Finds every collider along a ray, nearest first, one hit per collider.</summary>
    public RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity,
        LayerMask? layerMask = null, QueryTriggerInteraction queryTriggerInteraction = default)
    {
        if (World is null || !TryDirection(direction, maxDistance, out var unit, out var length))
        {
            return [];
        }

        var collector = new AllHitsCollector();
        World.CastRay(new RaycastInput(origin, origin + unit * length, FilterOf(layerMask),
            HitsTriggers(queryTriggerInteraction)), ref collector);
        return ToHits(collector, length);
    }

    /// <summary>Writes the nearest ray hits into <paramref name="results"/> and returns how many were written.</summary>
    public int RaycastNonAlloc(Vector3 origin, Vector3 direction, Span<RaycastHit> results,
        float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default)
    {
        var hits = RaycastAll(origin, direction, maxDistance, layerMask, queryTriggerInteraction);
        var count = Math.Min(hits.Length, results.Length);
        hits.AsSpan(0, count).CopyTo(results);
        return count;
    }

    /// <summary>Sweeps a sphere and finds the nearest collider it meets.</summary>
    public bool SphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default) =>
        Cast(ShapeDesc.Sphere(radius), origin, Quaternion.Identity, direction, maxDistance, layerMask,
            queryTriggerInteraction, out hit);

    /// <summary>Sweeps a box and finds the nearest collider it meets.</summary>
    public bool BoxCast(Vector3 center, Vector3 halfExtents, Vector3 direction, out RaycastHit hit,
        Quaternion orientation, float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default) =>
        Cast(ShapeDesc.Box(halfExtents * 2f), center, orientation, direction, maxDistance, layerMask,
            queryTriggerInteraction, out hit);

    /// <summary>Sweeps a capsule between two sphere centers and finds the nearest collider it meets.</summary>
    public bool CapsuleCast(Vector3 point1, Vector3 point2, float radius, Vector3 direction, out RaycastHit hit,
        float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default)
    {
        var (shape, center, orientation) = Capsule(point1, point2, radius);
        return Cast(shape, center, orientation, direction, maxDistance, layerMask, queryTriggerInteraction, out hit);
    }

    /// <summary>Finds the colliders touching a sphere.</summary>
    public ColliderComponent[] OverlapSphere(Vector3 position, float radius, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default) =>
        Overlap(ShapeDesc.Sphere(radius), new PhysicsPose(position, Quaternion.Identity), layerMask,
            queryTriggerInteraction);

    /// <summary>Finds the colliders touching a box.</summary>
    public ColliderComponent[] OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation,
        LayerMask? layerMask = null, QueryTriggerInteraction queryTriggerInteraction = default) =>
        Overlap(ShapeDesc.Box(halfExtents * 2f), new PhysicsPose(center, orientation), layerMask,
            queryTriggerInteraction);

    /// <summary>Finds the colliders touching a capsule between two sphere centers.</summary>
    public ColliderComponent[] OverlapCapsule(Vector3 point0, Vector3 point1, float radius,
        LayerMask? layerMask = null, QueryTriggerInteraction queryTriggerInteraction = default)
    {
        var (shape, center, orientation) = Capsule(point0, point1, radius);
        return Overlap(shape, new PhysicsPose(center, orientation), layerMask, queryTriggerInteraction);
    }

    /// <summary>Whether any collider touches a sphere.</summary>
    public bool CheckSphere(Vector3 position, float radius, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default) =>
        OverlapSphere(position, radius, layerMask, queryTriggerInteraction).Length > 0;

    /// <summary>Whether any collider touches a box.</summary>
    public bool CheckBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, LayerMask? layerMask = null,
        QueryTriggerInteraction queryTriggerInteraction = default) =>
        OverlapBox(center, halfExtents, orientation, layerMask, queryTriggerInteraction).Length > 0;

    /// <summary>
    /// The smallest move that separates collider <paramref name="a"/>, placed at a pose, from collider
    /// <paramref name="b"/>; false when they do not overlap. A mesh collider is only supported as <paramref name="b"/>
    /// at its simulated pose.
    /// </summary>
    public bool ComputePenetration(ColliderComponent a, Vector3 positionA, Quaternion rotationA,
        ColliderComponent b, Vector3 positionB, Quaternion rotationB, out Vector3 direction, out float distance)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        direction = Vector3.Zero;
        distance = 0f;
        var (shapeA, poseA) = PlaceCollider(a, positionA, rotationA);
        var (shapeB, poseB) = PlaceCollider(b, positionB, rotationB);
        if (World is null || !shapeA.IsConvex)
        {
            return false;
        }

        var result = default(DistanceResult);
        var found = shapeB.IsConvex
            ? World.CalculateDistance(shapeA, poseA, shapeB, poseB, 0f, out result)
            : owners.TryGetValue(b, out var record) && record.Handle.IsValid
              && World.CalculateDistance(shapeA, poseA, record.Handle, b.PhysicsId, 0f, out result);
        if (!found || result.Distance >= 0f)
        {
            return false;
        }

        direction = result.Normal;
        distance = -result.Distance;
        return true;
    }

    IEnumerable<PhysicsHit> IPhysicsQueryBackend.RaycastAll(Ray ray, float maxDistance) =>
        RaycastAll(ray.Origin, ray.Direction, maxDistance).Where(static hit => hit.Node is not null)
            .Select(static hit => new PhysicsHit(hit.Node!, hit.Point, hit.Normal, hit.Distance));

    IEnumerable<Node> IPhysicsQueryBackend.OverlapBox(Bounds bounds) =>
        OverlapBox(bounds.Center, bounds.Size * 0.5f, Quaternion.Identity).Select(static c => c.Node)
            .OfType<Node>();

    IEnumerable<PhysicsHit> IPhysicsQueryBackend.BoxCastAll(Bounds bounds, Vector3 direction, float maxDistance)
    {
        if (World is null || !TryDirection(direction, maxDistance, out var unit, out var length))
        {
            return [];
        }

        var collector = new AllHitsCollector();
        World.CastShape(new ShapeCastInput(ShapeDesc.Box(bounds.Size), bounds.Center, Quaternion.Identity,
            bounds.Center + unit * length, CollisionFilter.Default, Settings.QueriesHitTriggers), ref collector);
        return ToHits(collector, length).Where(static hit => hit.Node is not null)
            .Select(static hit => new PhysicsHit(hit.Node!, hit.Point, hit.Normal, hit.Distance));
    }

    bool Cast(in ShapeDesc shape, Vector3 origin, Quaternion orientation, Vector3 direction, float maxDistance,
        LayerMask? layerMask, QueryTriggerInteraction triggers, out RaycastHit hit)
    {
        hit = default;
        if (World is null || !TryDirection(direction, maxDistance, out var unit, out var length))
        {
            return false;
        }

        var collector = new NearestHitCollector();
        World.CastShape(new ShapeCastInput(shape, origin, orientation, origin + unit * length, FilterOf(layerMask),
            HitsTriggers(triggers)), ref collector);
        return ToHit(collector, length, out hit);
    }

    ColliderComponent[] Overlap(in ShapeDesc shape, in PhysicsPose pose, LayerMask? layerMask,
        QueryTriggerInteraction triggers)
    {
        if (World is null)
        {
            return [];
        }

        var collector = new AllHitsCollector();
        World.Overlap(new OverlapInput(shape, pose, FilterOf(layerMask), HitsTriggers(triggers)), ref collector);
        return [.. collector.Hits.Select(hit => collidersById.GetValueOrDefault(hit.ColliderData)).OfType<ColliderComponent>()
            .Distinct()];
    }

    bool ToHit(in NearestHitCollector collector, float length, out RaycastHit hit)
    {
        hit = default;
        if (!collector.HasHit || !collidersById.TryGetValue(collector.Hit.ColliderData, out var collider))
        {
            return false;
        }

        hit = new RaycastHit(collector.Hit.Point, collector.Hit.Normal, collector.Hit.Fraction * length, collider);
        return true;
    }

    RaycastHit[] ToHits(AllHitsCollector collector, float length) =>
    [
        .. collector.Hits.Where(static hit => hit.Fraction > 0f)
            .GroupBy(static hit => hit.ColliderData)
            .Select(static group => group.MinBy(static hit => hit.Fraction))
            .Where(hit => collidersById.ContainsKey(hit.ColliderData))
            .Select(hit => new RaycastHit(hit.Point, hit.Normal, hit.Fraction * length, collidersById[hit.ColliderData]))
            .OrderBy(static hit => hit.Distance),
    ];

    static CollisionFilter FilterOf(LayerMask? mask) => new(uint.MaxValue, (mask ?? LayerMask.Everything).Value);

    bool HitsTriggers(QueryTriggerInteraction interaction) => interaction switch
    {
        QueryTriggerInteraction.Collide => true,
        QueryTriggerInteraction.Ignore => false,
        _ => Settings.QueriesHitTriggers,
    };

    static bool TryDirection(Vector3 direction, float maxDistance, out Vector3 unit, out float length)
    {
        length = float.IsFinite(maxDistance) ? MathF.Min(maxDistance, MaxQueryDistance) : MaxQueryDistance;
        var squared = direction.LengthSquared();
        unit = squared > 0f ? direction / MathF.Sqrt(squared) : Vector3.Zero;
        return squared > 0f && length > 0f;
    }

    static (ShapeDesc Shape, Vector3 Center, Quaternion Orientation) Capsule(Vector3 point0, Vector3 point1,
        float radius)
    {
        var axis = point1 - point0;
        var length = axis.Length();
        var orientation = length > 1e-6f ? FromTo(Vector3.UnitY, axis / length) : Quaternion.Identity;
        return (ShapeDesc.Capsule(radius, length), (point0 + point1) * 0.5f, orientation);
    }

    static (ShapeDesc Shape, PhysicsPose Pose) PlaceCollider(ColliderComponent collider, Vector3 position,
        Quaternion rotation)
    {
        var scale = collider.Node?.GlobalTransform.Scale ?? Vector3.One;
        var shape = collider.Describe(scale, out var offset);
        return (shape, new PhysicsPose(position + Vector3.Transform(collider.Center * scale, rotation),
            Quaternion.Normalize(rotation * offset)));
    }

    /// <summary>The shortest rotation turning one unit vector onto another.</summary>
    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f)
        {
            return Quaternion.Identity;
        }

        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from).LengthSquared() > 1e-6f ? Vector3.UnitX : Vector3.UnitZ;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(axis, from)), MathF.PI);
        }

        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
    }

    /// <summary>Keeps the nearest hit that does not start inside its collider, as Unity's queries do.</summary>
    struct NearestHitCollector() : IHitCollector
    {
        public bool HasHit { get; private set; }

        public PhysicsQueryHit Hit { get; private set; }

        public readonly bool EarlyOutOnFirstHit => false;

        public float MaxFraction { get; private set; } = 1f;

        public bool AddHit(in PhysicsQueryHit hit)
        {
            if (hit.Fraction <= 0f || hit.Fraction > MaxFraction)
            {
                return false;
            }

            MaxFraction = hit.Fraction;
            Hit = hit;
            HasHit = true;
            return true;
        }
    }
}
