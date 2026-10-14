namespace Turian.Engine.Physics;

/// <summary>
/// Axes along or around which a body may not move: linear locks use world axes, angular locks the body's local
/// axes. The values match Unity's <c>RigidbodyConstraints</c>.
/// </summary>
[Flags]
public enum AxisLocks
{
    /// <summary>The body moves freely.</summary>
    None = 0,

    /// <summary>No movement along X.</summary>
    LinearX = 1 << 1,

    /// <summary>No movement along Y.</summary>
    LinearY = 1 << 2,

    /// <summary>No movement along Z.</summary>
    LinearZ = 1 << 3,

    /// <summary>No rotation around X.</summary>
    AngularX = 1 << 4,

    /// <summary>No rotation around Y.</summary>
    AngularY = 1 << 5,

    /// <summary>No rotation around Z.</summary>
    AngularZ = 1 << 6,
}

/// <summary>One shape of a body, placed relative to the body's origin, with its own filter, surface and identity.</summary>
/// <param name="Shape">A convex shape, or a mesh when it is the body's only collider.</param>
/// <param name="LocalPose">The shape's pose relative to the body's origin.</param>
/// <param name="Filter">Which colliders this one may touch.</param>
/// <param name="Material">The surface response.</param>
/// <param name="IsTrigger">Reports overlaps instead of colliding.</param>
/// <param name="UserData">An identifier reported back in hits and events.</param>
public readonly record struct ColliderDesc(
    ShapeHandle Shape,
    PhysicsPose LocalPose,
    CollisionFilter Filter,
    MaterialDesc Material,
    bool IsTrigger,
    ulong UserData);

/// <summary>Mass properties of a moving body.</summary>
/// <param name="Mass">The total mass in kilograms, shared among colliders by volume.</param>
/// <param name="CenterOfMass">The center of mass relative to the body's origin, used unless automatic.</param>
/// <param name="AutomaticCenterOfMass">Derives the center of mass from the colliders.</param>
public readonly record struct PhysicsMass(float Mass, Vector3 CenterOfMass, bool AutomaticCenterOfMass = true)
{
    /// <summary>One kilogram with an automatic center of mass.</summary>
    public static PhysicsMass Default { get; } = new(1f, Vector3.Zero);
}

/// <summary>Describes a kinematic or dynamic body.</summary>
public readonly record struct BodyDesc
{
    /// <summary>Kinematic or dynamic; static descriptions use <see cref="StaticDesc"/>.</summary>
    public MotionType Motion { get; init; }

    /// <summary>The pose of the body's origin, which is its node's position and rotation.</summary>
    public PhysicsPose Pose { get; init; }

    /// <summary>The initial velocity.</summary>
    public PhysicsVelocity Velocity { get; init; }

    /// <summary>The body's shapes; several form one compound.</summary>
    public ColliderDesc[] Colliders { get; init; }

    /// <summary>Mass and center of mass; kinematic bodies ignore the mass.</summary>
    public PhysicsMass Mass { get; init; }

    /// <summary>The fraction of linear velocity lost per second.</summary>
    public float LinearDamping { get; init; }

    /// <summary>The fraction of angular velocity lost per second.</summary>
    public float AngularDamping { get; init; }

    /// <summary>The multiplier applied to the world's gravity; zero disables gravity.</summary>
    public float GravityScale { get; init; }

    /// <summary>Sweeps fast bodies to avoid tunneling, at extra cost.</summary>
    public bool Continuous { get; init; }

    /// <summary>The axes the body may not move along or rotate around.</summary>
    public AxisLocks Locks { get; init; }

    /// <summary>The speed in meters per second below which the body may fall asleep; negative never sleeps.</summary>
    public float SleepThreshold { get; init; }

    /// <summary>An identifier reported back with the body.</summary>
    public ulong UserData { get; init; }

    /// <summary>A dynamic body at <paramref name="pose"/> with default mass, damping and gravity.</summary>
    public static BodyDesc Dynamic(PhysicsPose pose, params ColliderDesc[] colliders) => new()
    {
        Motion = MotionType.Dynamic,
        Pose = pose,
        Colliders = colliders,
        Mass = PhysicsMass.Default,
        AngularDamping = 0.05f,
        GravityScale = 1f,
        SleepThreshold = 0.05f,
    };
}

/// <summary>Describes a body that never moves on its own.</summary>
/// <param name="Pose">The pose of the body's origin.</param>
/// <param name="Colliders">The body's shapes.</param>
/// <param name="UserData">An identifier reported back with the body.</param>
public readonly record struct StaticDesc(PhysicsPose Pose, ColliderDesc[] Colliders, ulong UserData = 0);
