namespace Turian.Engine.Physics;

/// <summary>Where a pair is in its contact lifetime.</summary>
public enum PhysicsEventState
{
    /// <summary>The pair started touching this step.</summary>
    Enter,

    /// <summary>The pair kept touching.</summary>
    Stay,

    /// <summary>The pair stopped touching, or one collider was removed.</summary>
    Exit,
}

/// <summary>One point of a contact manifold.</summary>
/// <param name="Point">The world contact position.</param>
/// <param name="Normal">The world normal, pointing from the second collider towards the first.</param>
/// <param name="Separation">The gap between the surfaces; negative when they penetrate.</param>
public readonly record struct ContactPointData(Vector3 Point, Vector3 Normal, float Separation);

/// <summary>Up to four contact points.</summary>
[InlineArray(4)]
public struct ContactPointBuffer
{
    ContactPointData first;
}

/// <summary>A solid collider pair's contact during the last step.</summary>
public struct ContactEvent
{
    /// <summary>The first collider's body.</summary>
    public BodyHandle BodyA;

    /// <summary>The second collider's body.</summary>
    public BodyHandle BodyB;

    /// <summary>The first collider's <see cref="ColliderDesc.UserData"/>.</summary>
    public ulong ColliderA;

    /// <summary>The second collider's <see cref="ColliderDesc.UserData"/>.</summary>
    public ulong ColliderB;

    /// <summary>Enter, stay or exit.</summary>
    public PhysicsEventState State;

    /// <summary>The velocity of the first collider relative to the second when the contact was found.</summary>
    public Vector3 RelativeVelocity;

    /// <summary>An estimate of the impulse that resolved the contact, pushing the first collider.</summary>
    public Vector3 Impulse;

    /// <summary>The contact points; exits carry none.</summary>
    public ContactPointBuffer Points;

    /// <summary>The number of valid entries in <see cref="Points"/>.</summary>
    public int PointCount;
}

/// <summary>A trigger pair's overlap during the last step; either collider may be the trigger.</summary>
/// <param name="BodyA">The first collider's body.</param>
/// <param name="BodyB">The second collider's body.</param>
/// <param name="ColliderA">The first collider's user data.</param>
/// <param name="ColliderB">The second collider's user data.</param>
/// <param name="State">Enter, stay or exit.</param>
public readonly record struct TriggerEvent(
    BodyHandle BodyA,
    BodyHandle BodyB,
    ulong ColliderA,
    ulong ColliderB,
    PhysicsEventState State);
