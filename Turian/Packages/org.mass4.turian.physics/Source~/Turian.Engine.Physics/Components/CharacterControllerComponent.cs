namespace Turian.Engine.Physics;

/// <summary>
/// Moves a character as an upright capsule that slides along what it meets, climbs steps up to
/// <see cref="StepOffset"/> and slopes up to <see cref="SlopeLimit"/>, and rides the platform it stands on. It moves
/// only through <see cref="Move"/> and <see cref="SimpleMove"/>; other bodies collide with it as with a static collider.
/// </summary>
/// <remarks>Like Unity's, it does not push rigidbodies; scripts do that in <see cref="IControllerColliderHitHandler"/>.</remarks>
[DisallowMultipleComponent]
[ComponentContextMenu("Physics/Character Controller")]
[TypeId("6072013b-e31e-4b64-8816-d957963233fd")]
public class CharacterControllerComponent : ColliderComponent
{
    const int MaxSlides = 4;
    const float MinTravel = 1e-5f;

    readonly List<ControllerColliderHit> hits = [];
    ColliderComponent? ground;
    Matrix4x4 groundPose;
    float fallSpeed;

    /// <summary>The capsule's radius.</summary>
    public float Radius
    {
        get;
        set => Change(ref field, MathF.Max(value, 0.01f));
    } = 0.5f;

    /// <summary>The capsule's total height, including both ends.</summary>
    public float Height
    {
        get;
        set => Change(ref field, MathF.Max(value, 0.02f));
    } = 2f;

    /// <summary>The steepest slope, in degrees from horizontal, the character walks up.</summary>
    public float SlopeLimit { get; set; } = 45f;

    /// <summary>The highest step, in meters, the character climbs without jumping.</summary>
    public float StepOffset { get; set; } = 0.3f;

    /// <summary>The gap kept between the capsule and what it touches, so sweeps never start inside geometry.</summary>
    public float SkinWidth { get; set; } = 0.02f;

    /// <summary>Moves shorter than this are skipped, saving work when the character stands still.</summary>
    public float MinMoveDistance { get; set; } = 0.001f;

    /// <summary>Whether the last move ended on walkable ground.</summary>
    [JsonIgnore, Hide]
    public bool IsGrounded => (CollisionFlags & CollisionFlags.Below) != 0;

    /// <summary>The sides that touched something during the last move.</summary>
    [JsonIgnore, Hide]
    public CollisionFlags CollisionFlags { get; private set; }

    /// <summary>The velocity of the last <see cref="SimpleMove"/>, falling included.</summary>
    [JsonIgnore, Hide]
    public Vector3 Velocity { get; private set; }

    /// <inheritdoc />
    /// <remarks>Always upright, whatever the node's rotation and scale.</remarks>
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Conjugate(Node?.GlobalTransform.Orientation ?? Quaternion.Identity);
        return CapsuleShape();
    }

    /// <summary>
    /// Moves by <paramref name="motion"/> in world space, sliding along walls, climbing steps and walkable slopes, and
    /// staying out of the ground; does nothing without a simulating physics scene.
    /// </summary>
    /// <returns>The sides that touched something.</returns>
    public CollisionFlags Move(Vector3 motion)
    {
        if (Physics?.World is not { } world || Node is not { } node)
        {
            return CollisionFlags = CollisionFlags.None;
        }

        hits.Clear();
        var wasGrounded = IsGrounded;
        var center = RideGround(node.GlobalTransform.Position + Center);
        center = Depenetrate(world, center);
        var flags = CollisionFlags.None;
        if (motion.LengthSquared() >= MinMoveDistance * MinMoveDistance)
        {
            var horizontal = motion with { Y = 0f };
            var lift = wasGrounded && horizontal.LengthSquared() > 0f ? StepOffset : 0f;
            var raised = Slide(world, ref center, new Vector3(0, MathF.Max(motion.Y, 0f) + lift, 0), Phase.Up, ref flags);
            Slide(world, ref center, horizontal, Phase.Across, ref flags);
            var snap = wasGrounded && motion.Y <= 0f ? StepOffset : 0f;
            var drop = MathF.Min(motion.Y, 0f) - MathF.Min(raised, lift);
            Descend(world, ref center, drop, snap, ref flags);
        }

        node.GlobalTransform = node.GlobalTransform with { Position = center - Center };
        CollisionFlags = flags;
        TrackGround();
        Report();
        return flags;
    }

    /// <summary>
    /// Walks at <paramref name="speed"/> meters per second, ignoring its vertical part, and falls under the scene's
    /// gravity while airborne.
    /// </summary>
    /// <param name="speed">The walking velocity.</param>
    /// <param name="deltaTime">The seconds the move covers.</param>
    /// <returns>Whether the character ended on the ground.</returns>
    public bool SimpleMove(Vector3 speed, float deltaTime)
    {
        var gravity = Physics?.Gravity.Y ?? 0f;
        fallSpeed = IsGrounded ? gravity * deltaTime : fallSpeed + gravity * deltaTime;
        Velocity = new Vector3(speed.X, fallSpeed, speed.Z);
        Move(Velocity * deltaTime);
        if (IsGrounded)
        {
            fallSpeed = 0f;
        }

        return IsGrounded;
    }

    enum Phase
    {
        Up,
        Across,
        Down,
    }

    ShapeDesc CapsuleShape() => ShapeDesc.Capsule(Radius, MathF.Max(0f, Height - 2f * Radius));

    // Follows the platform the last move ended on, so the character rides it.
    Vector3 RideGround(Vector3 center)
    {
        if (ground?.Node is not { } platform)
        {
            return center;
        }

        var now = platform.GlobalTransform.Matrix4X4();
        if (now == groundPose || !Matrix4x4.Invert(groundPose, out var inverse))
        {
            return center;
        }

        return Vector3.Transform(Vector3.Transform(center, inverse), now);
    }

    void TrackGround()
    {
        ground = IsGrounded ? hits.LastOrDefault(h => Walkable(h.Normal)).Collider : null;
        groundPose = ground?.Node?.GlobalTransform.Matrix4X4() ?? Matrix4x4.Identity;
    }

    // Pushes the capsule out of whatever overlaps it, such as a platform that moved into it.
    Vector3 Depenetrate(IPhysicsWorld world, Vector3 center)
    {
        var shape = CapsuleShape();
        for (var pass = 0; pass < MaxSlides; pass++)
        {
            var overlaps = new AllHitsCollector();
            world.Overlap(new OverlapInput(shape, new PhysicsPose(center, Quaternion.Identity), Physics!.FilterOf(this)),
                ref overlaps);
            var pushed = false;
            foreach (var overlap in overlaps.Hits.Where(h => h.ColliderData != PhysicsId).DistinctBy(static h => h.ColliderData))
            {
                if (world.CalculateDistance(shape, new PhysicsPose(center, Quaternion.Identity), overlap.Body,
                        overlap.ColliderData, 0f, out var result) && result.Distance < 0f)
                {
                    center += result.Normal * (SkinWidth - result.Distance);
                    pushed = true;
                }
            }

            if (!pushed)
            {
                break;
            }
        }

        return center;
    }

    // Lowers the capsule, snapping onto ground within the extra snap distance but never falling by it.
    void Descend(IPhysicsWorld world, ref Vector3 center, float drop, float snap, ref CollisionFlags flags)
    {
        var start = center;
        var landed = CollisionFlags.None;
        Slide(world, ref center, new Vector3(0, drop - snap, 0), Phase.Down, ref landed);
        if ((landed & CollisionFlags.Below) == 0 && snap > 0f)
        {
            center = start;
            landed = CollisionFlags.None;
            Slide(world, ref center, new Vector3(0, drop, 0), Phase.Down, ref landed);
        }

        flags |= landed;
    }

    // Collide and slide: travels until blocked, then follows the surface with what is left. Returns the distance
    // travelled.
    float Slide(IPhysicsWorld world, ref Vector3 center, Vector3 delta, Phase phase, ref CollisionFlags flags)
    {
        var travelled = 0f;
        var shape = CapsuleShape();
        var filter = Physics!.FilterOf(this);
        for (var slide = 0; slide < MaxSlides; slide++)
        {
            var length = delta.Length();
            if (length < MinTravel)
            {
                break;
            }

            var direction = delta / length;
            var collector = new MoveCollector(PhysicsId, direction);
            world.CastShape(new ShapeCastInput(shape, center, Quaternion.Identity, center + delta, filter), ref collector);
            if (!collector.HasHit)
            {
                center += delta;
                travelled += length;
                break;
            }

            var hit = collector.Hit;
            var advance = MathF.Max(0f, hit.Fraction * length - SkinWidth);
            center += direction * advance;
            travelled += advance;
            var normal = hit.Normal;
            var landed = phase == Phase.Down && (Walkable(normal) || OnStepEdge(world, center, hit, filter));
            flags |= landed ? CollisionFlags.Below : Classify(normal);
            Record(hit, direction, length - advance);
            if (landed)
            {
                break;
            }

            // Too steep to walk: a wall, so the character cannot climb it by walking into it.
            if (phase == Phase.Across && !Walkable(normal))
            {
                normal = normal with { Y = 0f };
                if (normal.LengthSquared() < 1e-8f)
                {
                    break;
                }

                normal = Vector3.Normalize(normal);
            }

            var remaining = direction * (length - advance);
            delta = remaining - normal * Vector3.Dot(remaining, normal);
        }

        return travelled;
    }

    // The rounded bottom touching a step's edge reports a tilted normal; a ray just past the edge, towards the step,
    // finds its walkable top, while on a steep slope it finds the slope again.
    bool OnStepEdge(IPhysicsWorld world, Vector3 center, in PhysicsQueryHit hit, CollisionFilter filter)
    {
        var outward = (hit.Point - center) with { Y = 0f };
        var feet = center.Y - Height * 0.5f;
        if (outward.LengthSquared() < 1e-8f || hit.Point.Y > feet + StepOffset + SkinWidth)
        {
            return false;
        }

        var probe = hit.Point + Vector3.Normalize(outward) * (2f * SkinWidth) + new Vector3(0, SkinWidth * 2f, 0);
        var collector = new ClosestHitCollector();
        world.CastRay(new RaycastInput(probe, probe - new Vector3(0, SkinWidth * 4f, 0), filter), ref collector);
        return collector.HasHit && collector.Hit.ColliderData != PhysicsId && Walkable(collector.Hit.Normal);
    }

    CollisionFlags Classify(Vector3 normal) =>
        Walkable(normal) ? CollisionFlags.Below
        : normal.Y < -0.7f ? CollisionFlags.Above
        : CollisionFlags.Sides;

    bool Walkable(Vector3 normal) => normal.Y >= MathF.Cos(SlopeLimit * (MathF.PI / 180f)) - 1e-4f;

    void Record(in PhysicsQueryHit hit, Vector3 direction, float remaining)
    {
        if (Physics!.ColliderOf(hit.ColliderData) is { } collider)
        {
            hits.Add(new ControllerColliderHit(this, collider, hit.Point, hit.Normal, direction, remaining));
        }
    }

    void Report()
    {
        if (Node is not { } node)
        {
            return;
        }

        foreach (var handler in node.Components.OfType<IControllerColliderHitHandler>())
        {
            foreach (var hit in hits)
            {
                handler.OnControllerColliderHit(hit);
            }
        }
    }

    /// <summary>
    /// Keeps the nearest hit that blocks the move: the controller's own collider is skipped, and so are surfaces it
    /// moves away from and those it starts in, which <see cref="Depenetrate"/> already resolved.
    /// </summary>
    struct MoveCollector(ulong self, Vector3 direction) : IHitCollector
    {
        public bool HasHit { get; private set; }

        public PhysicsQueryHit Hit { get; private set; }

        public readonly bool EarlyOutOnFirstHit => false;

        public float MaxFraction { get; private set; } = 1f;

        public bool AddHit(in PhysicsQueryHit hit)
        {
            if (hit.ColliderData == self || hit.Fraction <= 0f || hit.Fraction > MaxFraction ||
                Vector3.Dot(hit.Normal, direction) >= 0f)
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
