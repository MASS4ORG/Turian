namespace Turian.Engine.Physics;

/// <summary>
/// Puts a node under physics control: it falls, collides through its colliders and the colliders of its
/// descendants, and responds to forces. Kinematic bodies are moved by scripts and push dynamic ones.
/// </summary>
[DisallowMultipleComponent]
[ComponentContextMenu("Physics/Rigidbody")]
[TypeId("ed053cc6-a437-4a75-b33c-68a92ef6e453")]
public class RigidbodyComponent : Component
{
    PhysicsVelocity detachedVelocity;

    /// <summary>The mass in kilograms.</summary>
    public float Mass
    {
        get;
        set => Change(ref field, MathF.Max(value, 1e-4f));
    } = 1f;

    /// <summary>The fraction of linear velocity lost per second.</summary>
    public float LinearDamping
    {
        get;
        set => Change(ref field, MathF.Max(value, 0f));
    }

    /// <summary>The fraction of angular velocity lost per second.</summary>
    public float AngularDamping
    {
        get;
        set => Change(ref field, MathF.Max(value, 0f));
    } = 0.05f;

    /// <summary>Whether gravity accelerates the body.</summary>
    public bool UseGravity
    {
        get;
        set => Change(ref field, value);
    } = true;

    /// <summary>The multiplier applied to gravity for this body.</summary>
    public float GravityScale
    {
        get;
        set => Change(ref field, value);
    } = 1f;

    /// <summary>Whether scripts move the body instead of forces; kinematic bodies push dynamic ones.</summary>
    public bool IsKinematic
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>How the node follows the simulation between fixed ticks.</summary>
    public RigidbodyInterpolation Interpolation { get; set; }

    /// <summary>How the body detects collisions.</summary>
    public CollisionDetectionMode CollisionDetectionMode
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The axes the body may not move along or rotate around.</summary>
    public RigidbodyConstraints Constraints
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The center of mass relative to the node, used when <see cref="AutomaticCenterOfMass"/> is off.</summary>
    public Vector3 CenterOfMass
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>Derives the center of mass from the colliders' volumes.</summary>
    public bool AutomaticCenterOfMass
    {
        get;
        set => Change(ref field, value);
    } = true;

    /// <summary>The physics service of the running scene, or null in hosts without physics.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public PhysicsScene? Physics { get; private set; }

    /// <summary>The velocity of the center of mass in meters per second.</summary>
    [JsonIgnore, Hide]
    public Vector3 LinearVelocity
    {
        get => Velocity.Linear;
        set => Velocity = Velocity with { Linear = value };
    }

    /// <summary>The angular velocity in radians per second around each world axis.</summary>
    [JsonIgnore, Hide]
    public Vector3 AngularVelocity
    {
        get => Velocity.Angular;
        set => Velocity = Velocity with { Angular = value };
    }

    /// <summary>The body's position as the simulation sees it, ignoring interpolation.</summary>
    [JsonIgnore, Hide]
    public Vector3 Position => Physics?.GetPose(this)?.Position ?? Node?.GlobalTransform.Position ?? Vector3.Zero;

    /// <summary>The body's orientation as the simulation sees it, ignoring interpolation.</summary>
    [JsonIgnore, Hide]
    public Quaternion Rotation =>
        Physics?.GetPose(this)?.Orientation ?? Node?.GlobalTransform.Orientation ?? Quaternion.Identity;

    /// <summary>The world center of mass.</summary>
    [JsonIgnore, Hide]
    public Vector3 WorldCenterOfMass => Physics?.GetCenterOfMass(this) ?? Position;

    PhysicsVelocity Velocity
    {
        get => Physics?.GetVelocity(this) ?? detachedVelocity;
        set
        {
            detachedVelocity = value;
            Physics?.SetVelocity(this, value);
        }
    }

    /// <summary>The velocity last set while the body had no simulation, applied when its body is built.</summary>
    internal PhysicsVelocity DetachedVelocity => detachedVelocity;

    /// <summary>Forces and moves queued for the next fixed tick.</summary>
    internal RigidbodyCommands Commands { get; } = new();

    /// <summary>Adds a force at the center of mass.</summary>
    public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force) => Commands.AddLinear(force, mode, Mass);

    /// <summary>Adds a torque.</summary>
    public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force) => Commands.AddAngular(torque, mode);

    /// <summary>Adds a force at a world position, which also turns the body.</summary>
    public void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode = ForceMode.Force) =>
        Commands.AddAtPosition(force, position, mode, Mass);

    /// <summary>
    /// Pushes the body away from an explosion, weakening linearly to nothing at <paramref name="radius"/>;
    /// <paramref name="upwardsModifier"/> lowers the explosion to lift bodies.
    /// </summary>
    public void AddExplosionForce(float explosionForce, Vector3 explosionPosition, float radius,
        float upwardsModifier = 0f, ForceMode mode = ForceMode.Force)
    {
        var center = WorldCenterOfMass;
        var origin = explosionPosition - new Vector3(0f, upwardsModifier, 0f);
        var offset = center - origin;
        var distance = offset.Length();
        if (radius > 0f && distance >= radius)
        {
            return;
        }

        var falloff = radius > 0f ? 1f - distance / radius : 1f;
        var direction = distance > 1e-6f ? offset / distance : Vector3.UnitY;
        AddForceAtPosition(direction * explosionForce * falloff, center, mode);
    }

    /// <summary>Moves a kinematic body over the next fixed tick so it pushes what it meets; teleports others.</summary>
    public void MovePosition(Vector3 position) => Commands.TargetPosition = position;

    /// <summary>Rotates a kinematic body over the next fixed tick; teleports others.</summary>
    public void MoveRotation(Quaternion rotation) => Commands.TargetRotation = rotation;

    /// <summary>Puts the body to sleep until something touches it.</summary>
    public void Sleep() => Physics?.Sleep(this);

    /// <summary>Wakes the body.</summary>
    public void WakeUp() => Physics?.WakeUp(this);

    /// <summary>Whether the body is asleep; bodies outside a simulation count as asleep.</summary>
    public bool IsSleeping() => Physics?.IsSleeping(this) ?? true;

    /// <summary>The velocity of the body at a world point, including rotation.</summary>
    public Vector3 GetPointVelocity(Vector3 worldPoint)
    {
        var velocity = Velocity;
        return velocity.Linear + Vector3.Cross(velocity.Angular, worldPoint - WorldCenterOfMass);
    }

    /// <inheritdoc />
    public override void OnEnable() => Physics?.Register(this);

    /// <inheritdoc />
    public override void OnDisable() => Physics?.Unregister(this);

    void Change<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Physics?.MarkDirty(this);
    }
}

/// <summary>Forces and moves a rigidbody queued for the next fixed tick.</summary>
sealed class RigidbodyCommands
{
    /// <summary>Continuous linear force in newtons.</summary>
    public Vector3 Force;

    /// <summary>Instant linear impulse in newton-seconds.</summary>
    public Vector3 Impulse;

    /// <summary>Continuous torque.</summary>
    public Vector3 Torque;

    /// <summary>Instant angular impulse.</summary>
    public Vector3 AngularImpulse;

    /// <summary>Continuous angular acceleration, independent of inertia.</summary>
    public Vector3 AngularAcceleration;

    /// <summary>Instant angular velocity change, independent of inertia.</summary>
    public Vector3 AngularVelocityChange;

    /// <summary>Forces applied away from the center of mass.</summary>
    public List<(Vector3 Force, Vector3 Point, bool Continuous)> PointForces { get; } = [];

    /// <summary>A position to reach over the next tick.</summary>
    public Vector3? TargetPosition;

    /// <summary>An orientation to reach over the next tick.</summary>
    public Quaternion? TargetRotation;

    /// <summary>Whether any force is queued.</summary>
    public bool HasForces => Force != Vector3.Zero || Impulse != Vector3.Zero || Torque != Vector3.Zero ||
                             AngularImpulse != Vector3.Zero || AngularAcceleration != Vector3.Zero ||
                             AngularVelocityChange != Vector3.Zero || PointForces.Count > 0;

    public void AddLinear(Vector3 force, ForceMode mode, float mass)
    {
        switch (mode)
        {
            case ForceMode.Force: Force += force; break;
            case ForceMode.Acceleration: Force += force * mass; break;
            case ForceMode.Impulse: Impulse += force; break;
            default: Impulse += force * mass; break;
        }
    }

    public void AddAngular(Vector3 torque, ForceMode mode)
    {
        switch (mode)
        {
            case ForceMode.Force: Torque += torque; break;
            case ForceMode.Acceleration: AngularAcceleration += torque; break;
            case ForceMode.Impulse: AngularImpulse += torque; break;
            default: AngularVelocityChange += torque; break;
        }
    }

    public void AddAtPosition(Vector3 force, Vector3 position, ForceMode mode, float mass)
    {
        var scaled = mode is ForceMode.Acceleration or ForceMode.VelocityChange ? force * mass : force;
        PointForces.Add((scaled, position, mode is ForceMode.Force or ForceMode.Acceleration));
    }

    public void ClearForces()
    {
        Force = Impulse = Torque = AngularImpulse = AngularAcceleration = AngularVelocityChange = Vector3.Zero;
        PointForces.Clear();
    }
}
