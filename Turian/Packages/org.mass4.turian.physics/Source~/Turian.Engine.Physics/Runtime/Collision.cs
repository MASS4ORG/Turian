namespace Turian.Engine.Physics;

/// <summary>One point where two colliders touch.</summary>
/// <param name="Point">The world position.</param>
/// <param name="Normal">The world normal, pointing from the other collider towards this one.</param>
/// <param name="Separation">The gap between the surfaces; negative when they penetrate.</param>
/// <param name="ThisCollider">The collider receiving the callback.</param>
/// <param name="OtherCollider">The collider it touches.</param>
public readonly record struct ContactPoint(
    Vector3 Point,
    Vector3 Normal,
    float Separation,
    ColliderComponent ThisCollider,
    ColliderComponent OtherCollider);

/// <summary>Describes a collision to the component receiving it, as Unity's <c>Collision</c> does.</summary>
public sealed class Collision
{
    internal Collision(ColliderComponent collider, ColliderComponent other, ContactPoint[] contacts, Vector3 impulse,
        Vector3 relativeVelocity)
    {
        ThisCollider = collider;
        Collider = other;
        Contacts = contacts;
        Impulse = impulse;
        RelativeVelocity = relativeVelocity;
    }

    /// <summary>The collider of the receiving side.</summary>
    public ColliderComponent ThisCollider { get; }

    /// <summary>The other collider.</summary>
    public ColliderComponent Collider { get; }

    /// <summary>The other collider's body, or null for a static collider.</summary>
    public RigidbodyComponent? Rigidbody => Collider.AttachedRigidbody;

    /// <summary>The other collider's node.</summary>
    public Node? Node => Collider.Node;

    /// <summary>The contact points; empty on exit.</summary>
    public IReadOnlyList<ContactPoint> Contacts { get; }

    /// <summary>The number of contact points.</summary>
    public int ContactCount => Contacts.Count;

    /// <summary>An estimate of the impulse that pushed the receiving side, in newton-seconds.</summary>
    public Vector3 Impulse { get; }

    /// <summary>The velocity of the receiving side relative to the other at the first contact.</summary>
    public Vector3 RelativeVelocity { get; }

    /// <summary>The contact point at <paramref name="index"/>.</summary>
    public ContactPoint GetContact(int index) => Contacts[index];
}

/// <summary>Receives collisions of the colliders on its node and of the body it belongs to.</summary>
public interface ICollisionHandler
{
    /// <summary>The colliders started touching.</summary>
    void OnCollisionEnter(Collision collision)
    {
    }

    /// <summary>The colliders kept touching during the last fixed tick.</summary>
    void OnCollisionStay(Collision collision)
    {
    }

    /// <summary>The colliders stopped touching.</summary>
    void OnCollisionExit(Collision collision)
    {
    }
}

/// <summary>Receives trigger overlaps of the colliders on its node and of the body it belongs to.</summary>
public interface ITriggerHandler
{
    /// <summary>The other collider entered a trigger, or this trigger entered it.</summary>
    void OnTriggerEnter(ColliderComponent other)
    {
    }

    /// <summary>The overlap continued during the last fixed tick.</summary>
    void OnTriggerStay(ColliderComponent other)
    {
    }

    /// <summary>The overlap ended.</summary>
    void OnTriggerExit(ColliderComponent other)
    {
    }
}

/// <summary>A hit returned by the Unity-style queries of <see cref="PhysicsScene"/>.</summary>
/// <param name="Point">The world hit position.</param>
/// <param name="Normal">The world surface normal at the hit.</param>
/// <param name="Distance">The distance travelled from the query's origin.</param>
/// <param name="Collider">The collider hit.</param>
public readonly record struct RaycastHit(Vector3 Point, Vector3 Normal, float Distance, ColliderComponent Collider)
{
    /// <summary>The body of the collider hit, or null for a static collider.</summary>
    public RigidbodyComponent? Rigidbody => Collider.AttachedRigidbody;

    /// <summary>The node of the collider hit.</summary>
    public Node? Node => Collider.Node;
}
