namespace Turian.Engine.Physics;

/// <summary>
/// Connects the rigidbody on this node, or on its nearest ancestor, to another rigidbody or to the world. Angles,
/// offsets and motors are of this body relative to the connected one, measured from the pose at creation; positive
/// angles turn counter-clockwise around the axis.
/// </summary>
public abstract class JointComponent : Component
{
    Vector3 connectedAnchor;

    /// <summary>The body this one is attached to; null attaches it to a fixed point in the world.</summary>
    public RigidbodyComponent? ConnectedBody
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The attachment point in this node's local space.</summary>
    public Vector3 Anchor
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>
    /// The attachment point in the connected body's local space, or in world space without one. Overwritten when the
    /// joint is created while <see cref="AutoConfigureConnectedAnchor"/> is on.
    /// </summary>
    public Vector3 ConnectedAnchor
    {
        get => connectedAnchor;
        set => Change(ref connectedAnchor, value);
    }

    /// <summary>Places <see cref="ConnectedAnchor"/> where <see cref="Anchor"/> is when the joint is created.</summary>
    public bool AutoConfigureConnectedAnchor
    {
        get;
        set => Change(ref field, value);
    } = true;

    /// <summary>Whether the connected bodies still collide with each other.</summary>
    public bool EnableCollision
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The physics service of the running scene, or null in hosts without physics.</summary>
    /// <remarks>Not private: reflection on a derived joint type cannot see a base class's private setter.</remarks>
    [InjectService(Optional = true), JsonIgnore]
    public PhysicsScene? Physics { get; internal set; }

    /// <summary>The body this joint moves: the nearest enabled rigidbody on its node or an ancestor.</summary>
    [JsonIgnore, Hide]
    public RigidbodyComponent? Body => PhysicsScene.FindRigidbody(Node);

    /// <summary>Records the connected anchor a new joint placed, without recreating the joint.</summary>
    internal void ConnectedAnchorAtCreation(Vector3 anchor) => connectedAnchor = anchor;

    /// <summary>The joint's type and its settings; anchors and frames are filled in by the scene.</summary>
    /// <param name="worldAxis">The joint axis in world space, for joints that have one.</param>
    public abstract JointDesc Describe(out Vector3 worldAxis);

    /// <inheritdoc />
    public override void OnEnable() => Physics?.Register(this);

    /// <inheritdoc />
    public override void OnDisable() => Physics?.Unregister(this);

    /// <summary>Sets a property and recreates the joint when the value changed.</summary>
    protected void Change<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Physics?.MarkDirty(this);
    }

    /// <summary>A local axis turned into world space by this node's rotation.</summary>
    protected Vector3 WorldAxis(Vector3 axis)
    {
        var unit = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitX;
        return Vector3.Transform(unit, Node?.GlobalTransform.Orientation ?? Quaternion.Identity);
    }

    /// <summary>Degrees to radians.</summary>
    protected static float Radians(float degrees) => degrees * (MathF.PI / 180f);
}
