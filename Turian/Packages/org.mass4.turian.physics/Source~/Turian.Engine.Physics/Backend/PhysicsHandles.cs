namespace Turian.Engine.Physics;

/// <summary>Identifies a body, static or moving, in one <see cref="IPhysicsWorld"/>; the default value is invalid.</summary>
/// <param name="Id">The world's identifier, starting at 1.</param>
public readonly record struct BodyHandle(int Id)
{
    /// <summary>Whether the handle refers to a created body.</summary>
    public bool IsValid => Id > 0;
}

/// <summary>Identifies a collision shape in one <see cref="IPhysicsWorld"/>; the default value is invalid.</summary>
/// <param name="Id">The world's identifier, starting at 1.</param>
public readonly record struct ShapeHandle(int Id)
{
    /// <summary>Whether the handle refers to a created shape.</summary>
    public bool IsValid => Id > 0;
}

/// <summary>Identifies a joint in one <see cref="IPhysicsWorld"/>; the default value is invalid.</summary>
/// <param name="Id">The world's identifier, starting at 1.</param>
public readonly record struct JointHandle(int Id)
{
    /// <summary>Whether the handle refers to a created joint.</summary>
    public bool IsValid => Id > 0;
}

/// <summary>How a body moves.</summary>
public enum MotionType
{
    /// <summary>Never moves unless teleported; costs nothing while idle.</summary>
    Static,

    /// <summary>Moved by its velocity or kinematic target only; pushes dynamic bodies and is never pushed.</summary>
    Kinematic,

    /// <summary>Moved by gravity, forces and contacts.</summary>
    Dynamic,
}

/// <summary>A position and orientation without scale; physics shapes carry their size instead.</summary>
/// <param name="Position">The world position.</param>
/// <param name="Orientation">The world orientation.</param>
public readonly record struct PhysicsPose(Vector3 Position, Quaternion Orientation)
{
    /// <summary>The pose at the origin with no rotation.</summary>
    public static PhysicsPose Identity { get; } = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>Transforms a point from this pose's local space to world space.</summary>
    public Vector3 TransformPoint(Vector3 local) => Position + Vector3.Transform(local, Orientation);

    /// <summary>Transforms a world point into this pose's local space.</summary>
    public Vector3 InverseTransformPoint(Vector3 world) =>
        Vector3.Transform(world - Position, Quaternion.Conjugate(Orientation));

    /// <summary>Combines a pose local to this one into world space.</summary>
    public PhysicsPose Multiply(in PhysicsPose local) =>
        new(TransformPoint(local.Position), Quaternion.Normalize(Orientation * local.Orientation));

    /// <summary>The pose that undoes this one.</summary>
    public PhysicsPose Inverse()
    {
        var inverse = Quaternion.Conjugate(Orientation);
        return new PhysicsPose(Vector3.Transform(-Position, inverse), inverse);
    }
}

/// <summary>Linear and angular velocity of a body, in world space.</summary>
/// <param name="Linear">Meters per second of the center of mass.</param>
/// <param name="Angular">Radians per second around each world axis.</param>
public readonly record struct PhysicsVelocity(Vector3 Linear, Vector3 Angular);
