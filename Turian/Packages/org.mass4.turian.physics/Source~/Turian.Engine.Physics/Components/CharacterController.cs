namespace Turian.Engine.Physics;

/// <summary>Which sides of a character controller touched something during its last move, as in Unity.</summary>
[Flags]
public enum CollisionFlags
{
    /// <summary>Nothing was touched.</summary>
    None = 0,

    /// <summary>A wall or a slope too steep to walk.</summary>
    Sides = 1,

    /// <summary>A ceiling.</summary>
    Above = 2,

    /// <summary>Walkable ground.</summary>
    Below = 4,
}

/// <summary>A collider a character controller met while moving.</summary>
/// <param name="Controller">The controller that moved.</param>
/// <param name="Collider">The collider it met.</param>
/// <param name="Point">The world contact point.</param>
/// <param name="Normal">The surface normal facing the controller.</param>
/// <param name="MoveDirection">The direction the controller was moving in.</param>
/// <param name="MoveLength">How far it was still trying to move.</param>
public readonly record struct ControllerColliderHit(
    CharacterControllerComponent Controller,
    ColliderComponent Collider,
    Vector3 Point,
    Vector3 Normal,
    Vector3 MoveDirection,
    float MoveLength)
{
    /// <summary>The body of the collider met, or null for a static collider.</summary>
    public RigidbodyComponent? Rigidbody => Collider.AttachedRigidbody;

    /// <summary>The node of the collider met.</summary>
    public Node? Node => Collider.Node;
}

/// <summary>Receives the colliders a character controller on its node meets while moving.</summary>
public interface IControllerColliderHitHandler
{
    /// <summary>The controller met a collider during <see cref="CharacterControllerComponent.Move"/>.</summary>
    void OnControllerColliderHit(ControllerColliderHit hit);
}
