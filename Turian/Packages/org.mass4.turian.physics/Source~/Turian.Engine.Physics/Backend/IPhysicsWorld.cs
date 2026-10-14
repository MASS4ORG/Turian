namespace Turian.Engine.Physics;

/// <summary>
/// A physics simulation behind plain data handles, so any engine can implement it and any front-end, component or
/// data-oriented, can drive it. Worlds are used from one thread; <see cref="Step"/> may use workers internally.
/// </summary>
public interface IPhysicsWorld : IDisposable
{
    /// <summary>The acceleration applied to every dynamic body with a non-zero gravity scale.</summary>
    Vector3 Gravity { get; set; }

    /// <summary>The number of kinematic, dynamic and static bodies.</summary>
    int BodyCount { get; }

    /// <summary>Creates collision geometry bodies can share.</summary>
    ShapeHandle CreateShape(in ShapeDesc description);

    /// <summary>Frees geometry no body uses anymore.</summary>
    void RemoveShape(ShapeHandle shape);

    /// <summary>Creates a kinematic or dynamic body.</summary>
    BodyHandle CreateBody(in BodyDesc description);

    /// <summary>Creates a body that does not move on its own.</summary>
    BodyHandle CreateStatic(in StaticDesc description);

    /// <summary>Removes a body and every joint attached to it.</summary>
    void Remove(BodyHandle body);

    /// <summary>Whether the handle names a live body.</summary>
    bool Contains(BodyHandle body);

    /// <summary>The pose of the body's origin.</summary>
    PhysicsPose GetPose(BodyHandle body);

    /// <summary>Teleports the body's origin and wakes it.</summary>
    void SetPose(BodyHandle body, in PhysicsPose pose);

    /// <summary>The body's velocity; zero for statics.</summary>
    PhysicsVelocity GetVelocity(BodyHandle body);

    /// <summary>Replaces the body's velocity and wakes it.</summary>
    void SetVelocity(BodyHandle body, in PhysicsVelocity velocity);

    /// <summary>The world position of the body's center of mass.</summary>
    Vector3 GetCenterOfMass(BodyHandle body);

    /// <summary>The body's mass; zero for kinematics and statics.</summary>
    float GetMass(BodyHandle body);

    /// <summary>Applies an impulse at a world point and wakes the body.</summary>
    void ApplyImpulse(BodyHandle body, Vector3 impulse, Vector3 worldPoint);

    /// <summary>Applies an angular impulse and wakes the body.</summary>
    void ApplyAngularImpulse(BodyHandle body, Vector3 angularImpulse);

    /// <summary>
    /// Moves a kinematic body towards a pose over the next step through its velocity, so it pushes what it meets.
    /// The velocity is cleared after that step.
    /// </summary>
    void SetKinematicTarget(BodyHandle body, in PhysicsPose target, float deltaTime);

    /// <summary>Wakes a sleeping body.</summary>
    void Wake(BodyHandle body);

    /// <summary>Puts a body to sleep until something touches it.</summary>
    void Sleep(BodyHandle body);

    /// <summary>Whether the body is simulated; statics are never awake.</summary>
    bool IsAwake(BodyHandle body);

    /// <summary>Stops or restores contact between two colliders, by their user data.</summary>
    void SetPairIgnored(ulong colliderA, ulong colliderB, bool ignored);

    /// <summary>Creates a joint.</summary>
    JointHandle CreateJoint(in JointDesc description);

    /// <summary>Changes a joint's limits, motor, spring and anchors; changing the type or bodies recreates it.</summary>
    void UpdateJoint(JointHandle joint, in JointDesc description);

    /// <summary>Removes a joint.</summary>
    void RemoveJoint(JointHandle joint);

    /// <summary>Advances the simulation and refreshes <see cref="Contacts"/> and <see cref="Triggers"/>.</summary>
    void Step(float deltaTime);

    /// <summary>Reports colliders along a ray.</summary>
    void CastRay<TCollector>(in RaycastInput input, ref TCollector collector) where TCollector : IHitCollector;

    /// <summary>Reports colliders a moving convex shape meets.</summary>
    void CastShape<TCollector>(in ShapeCastInput input, ref TCollector collector) where TCollector : IHitCollector;

    /// <summary>Reports colliders a convex shape overlaps.</summary>
    void Overlap<TCollector>(in OverlapInput input, ref TCollector collector) where TCollector : IHitCollector;

    /// <summary>Finds the closest features of two convex shapes within a distance.</summary>
    bool CalculateDistance(in ShapeDesc shapeA, in PhysicsPose poseA, in ShapeDesc shapeB, in PhysicsPose poseB,
        float maxDistance, out DistanceResult result);

    /// <summary>Finds the closest features of a convex shape and a collider of a body within a distance.</summary>
    bool CalculateDistance(in ShapeDesc shape, in PhysicsPose pose, BodyHandle body, ulong collider,
        float maxDistance, out DistanceResult result);

    /// <summary>The solid contacts of the last step, including exits.</summary>
    ReadOnlySpan<ContactEvent> Contacts { get; }

    /// <summary>The trigger overlaps of the last step, including exits.</summary>
    ReadOnlySpan<TriggerEvent> Triggers { get; }
}
