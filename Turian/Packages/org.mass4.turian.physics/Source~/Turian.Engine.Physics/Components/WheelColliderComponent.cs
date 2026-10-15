namespace Turian.Engine.Physics;

/// <summary>
/// A vehicle wheel, as Unity's WheelCollider: a ray from <see cref="ColliderComponent.Center"/> down the node's -Y
/// finds the ground, a spring and damper hold the body up, and tyre friction drives, brakes and steers it. Forces go to
/// the nearest rigidbody up the hierarchy each fixed tick; the wheel itself has no shape and collides with nothing.
/// </summary>
/// <remarks>
/// The wheel rolls along the node's +Z and steers around its +Y; a positive <see cref="SteerAngle"/> turns right.
/// Sizes are in world units, whatever the node's scale. Each wheel carries an equal share of the body's mass.
/// </remarks>
[ComponentContextMenu("Physics/Wheel Collider")]
[TypeId("c819f669-4787-4377-8992-dd9091edea62")]
public class WheelColliderComponent : ColliderComponent
{
    const float Degrees = MathF.PI / 180f;

    WheelHit groundHit;
    float travel;
    float angularVelocity;
    float rollAngle;

    /// <summary>The wheel's radius in meters.</summary>
    public float Radius { get; set => field = MathF.Max(value, 0.01f); } = 0.5f;

    /// <summary>The wheel's mass in kilograms, which sets how fast torques spin it.</summary>
    public float Mass { get; set => field = MathF.Max(value, 0.01f); } = 20f;

    /// <summary>How far the wheel travels from fully compressed, at the center, to fully extended below it.</summary>
    public float SuspensionDistance { get; set => field = MathF.Max(value, 0f); } = 0.3f;

    /// <summary>The suspension spring's stiffness in newtons per meter.</summary>
    public float SuspensionSpring { get; set => field = MathF.Max(value, 0f); } = 35000f;

    /// <summary>The suspension damper in newton-seconds per meter.</summary>
    public float SuspensionDamper { get; set => field = MathF.Max(value, 0f); } = 4500f;

    /// <summary>Where the loaded wheel rests along its travel: 0 fully extended, 1 fully compressed.</summary>
    public float SuspensionTargetPosition { get; set => field = Math.Clamp(value, 0f, 1f); } = 0.5f;

    /// <summary>The tyre's grip along its rolling direction.</summary>
    public WheelFrictionCurve ForwardFriction { get; set; } = WheelFrictionCurve.Forward;

    /// <summary>The tyre's grip across its rolling direction.</summary>
    public WheelFrictionCurve SidewaysFriction { get; set; } = WheelFrictionCurve.Sideways;

    /// <summary>The engine torque on the wheel in newton-meters; positive drives forward.</summary>
    [JsonIgnore, Hide]
    public float MotorTorque { get; set; }

    /// <summary>The brake torque in newton-meters; it slows the wheel's spin in either direction.</summary>
    [JsonIgnore, Hide]
    public float BrakeTorque { get; set => field = MathF.Max(value, 0f); }

    /// <summary>The steering angle in degrees around the node's up axis; positive turns right.</summary>
    [JsonIgnore, Hide]
    public float SteerAngle { get; set; }

    /// <summary>Whether the wheel touched the ground during the last fixed tick.</summary>
    [JsonIgnore, Hide]
    public bool IsGrounded { get; private set; }

    /// <summary>The wheel's spin in revolutions per minute.</summary>
    [JsonIgnore, Hide]
    public float Rpm => angularVelocity * 60f / (2f * MathF.PI);

    /// <summary>The mass this wheel holds up: its share of the body's mass.</summary>
    [JsonIgnore, Hide]
    public float SprungMass => AttachedRigidbody is { } body ? body.Mass / Math.Max(1, WheelsOf(body)) : 0f;

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public override bool HasShape => false;

    /// <inheritdoc />
    /// <remarks>A wheel has no shape; it senses the ground with a ray.</remarks>
    public override ShapeDesc Describe(Vector3 scale, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        return ShapeDesc.ConvexHull([]);
    }

    /// <summary>Where the wheel's center is along its suspension and how it is turned, steered and rolled.</summary>
    /// <param name="position">The wheel's world center.</param>
    /// <param name="rotation">The wheel's world rotation.</param>
    public void GetWorldPose(out Vector3 position, out Quaternion rotation)
    {
        var global = Node?.GlobalTransform ?? Transform.Identity;
        var up = Vector3.Transform(Vector3.UnitY, global.Orientation);
        position = Vector3.Transform(Center, global.Matrix4X4()) - up * (IsGrounded ? travel : SuspensionDistance);
        rotation = global.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, SteerAngle * Degrees)
                   * Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollAngle);
    }

    /// <summary>The wheel's contact during the last fixed tick.</summary>
    /// <returns>False while the wheel is in the air.</returns>
    public bool GetGroundHit(out WheelHit hit)
    {
        hit = groundHit;
        return IsGrounded;
    }

    /// <inheritdoc />
    public override void OnFixedUpdate(float fixedDeltaTime)
    {
        IsGrounded = false;
        var inertia = 0.5f * Mass * Radius * Radius;
        Spin(fixedDeltaTime, inertia);
        if (fixedDeltaTime > 0f && Physics is { IsSimulating: true } physics && Node is { } node
            && AttachedRigidbody is { IsKinematic: false } body)
        {
            Drive(physics, node.GlobalTransform, body, fixedDeltaTime, inertia);
        }

        rollAngle = (rollAngle + angularVelocity * fixedDeltaTime) % (2f * MathF.PI);
    }

    // Motor and brake torques change the spin before the tyre meets the ground.
    void Spin(float deltaTime, float inertia)
    {
        angularVelocity += MotorTorque / inertia * deltaTime;
        var braking = BrakeTorque / inertia * deltaTime;
        angularVelocity = MathF.Abs(angularVelocity) <= braking
            ? 0f
            : angularVelocity - MathF.Sign(angularVelocity) * braking;
    }

    void Drive(PhysicsScene physics, Transform global, RigidbodyComponent body, float deltaTime, float inertia)
    {
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, global.Orientation));
        var origin = Vector3.Transform(Center, global.Matrix4X4());
        var reach = SuspensionDistance + Radius;
        if (!physics.RaycastIgnoring(origin, -up, reach, body, physics.FilterOf(this), out var ray))
        {
            return;
        }

        IsGrounded = true;
        travel = Math.Clamp(ray.Distance - Radius, 0f, SuspensionDistance);
        var sprung = MathF.Max(SprungMass, 1e-3f);
        var ground = ray.Rigidbody;
        var velocity = body.GetPointVelocity(ray.Point) - (ground?.GetPointVelocity(ray.Point) ?? Vector3.Zero);

        // The sprung weight plus the spring around its target rests the loaded wheel at the target.
        var gravity = body.UseGravity ? physics.Gravity * body.GravityScale : Vector3.Zero;
        var compression = SuspensionDistance - travel;
        var load = MathF.Max(0f, sprung * -Vector3.Dot(gravity, up)
                                 + SuspensionSpring * (compression - SuspensionTargetPosition * SuspensionDistance)
                                 - SuspensionDamper * Vector3.Dot(velocity, up));

        var steered = Vector3.Transform(Vector3.Transform(Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, SteerAngle * Degrees)), global.Orientation);
        var normal = ray.Normal;
        var forward = steered - normal * Vector3.Dot(steered, normal);
        forward = forward.LengthSquared() > 1e-8f
            ? Vector3.Normalize(forward)
            : Vector3.Normalize(Vector3.Cross(normal, up));
        var side = Vector3.Normalize(Vector3.Cross(normal, forward));

        // A braked wheel that stopped stays locked unless the road's torque beats the brake.
        var locked = angularVelocity == 0f && BrakeTorque > 0f;
        var (longitudinal, lateral) = Friction(Vector3.Dot(velocity, forward), Vector3.Dot(velocity, side), load,
            sprung, locked ? 0f : Radius * Radius / inertia, deltaTime);
        var spin = longitudinal * Radius / inertia * deltaTime;
        var hold = locked ? BrakeTorque / inertia * deltaTime : 0f;
        angularVelocity -= MathF.Sign(spin) * MathF.Max(0f, MathF.Abs(spin) - hold);

        // Applied at the wheel's center rather than the contact: tyre forces at ground level rock the body, and the
        // per-tick slip limit, which ignores rotation, would feed that rocking.
        var force = up * load + forward * longitudinal + side * lateral;
        var center = origin - up * travel;
        body.AddForceAtPosition(force, center);
        ground?.AddForceAtPosition(-force, ray.Point);
        groundHit = new WheelHit(ray.Point, normal, forward, side, load,
            angularVelocity * Radius - Vector3.Dot(velocity, forward), Vector3.Dot(velocity, side), ray.Collider);
    }

    // Each force is the curve's grip times the load, but never more than removes half the slip within the tick: a
    // whole-slip limit treats the body as a point mass and overshoots once wheels also turn it, so a parked car would
    // rock. Together the forces stay within the tyre's friction circle.
    (float Longitudinal, float Lateral) Friction(float forwardSpeed, float sideSpeed, float load, float sprung,
        float spinCompliance, float deltaTime)
    {
        const float Relaxation = 0.5f;
        var slip = angularVelocity * Radius - forwardSpeed;
        var stopSlip = Relaxation * MathF.Abs(slip) / (spinCompliance + 1f / sprung) / deltaTime;
        var longitudinal = MathF.Sign(slip) * MathF.Min(ForwardFriction.Evaluate(slip) * load, stopSlip);
        var lateral = -MathF.Sign(sideSpeed) * MathF.Min(SidewaysFriction.Evaluate(sideSpeed) * load,
            Relaxation * MathF.Abs(sideSpeed) * sprung / deltaTime);

        var limit = MathF.Max(ForwardFriction.Peak, SidewaysFriction.Peak) * load;
        var total = MathF.Sqrt(longitudinal * longitudinal + lateral * lateral);
        return total > limit && total > 0f
            ? (longitudinal * limit / total, lateral * limit / total)
            : (longitudinal, lateral);
    }

    static int WheelsOf(RigidbodyComponent body)
    {
        var count = 0;
        var pending = new Stack<Node>();
        if (body.Node is { } root)
        {
            pending.Push(root);
        }

        while (pending.TryPop(out var node))
        {
            foreach (var component in node.Components)
            {
                if (component is WheelColliderComponent { IsActive: true } wheel && wheel.AttachedRigidbody == body)
                {
                    count++;
                }
            }

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        return count;
    }
}
