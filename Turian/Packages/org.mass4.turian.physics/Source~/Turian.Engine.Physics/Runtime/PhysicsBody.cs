namespace Turian.Engine.Physics;

/// <summary>The backend body behind a rigidbody, or behind one static collider, and what it was built from.</summary>
sealed class PhysicsBody(Node node, RigidbodyComponent? rigidbody)
{
    // Pose writes round-trip through local transforms, so geometry compares within a tolerance.
    const float Tolerance = 1e-4f;

    /// <summary>The node whose transform is the body's origin.</summary>
    public Node Node { get; } = node;

    /// <summary>The rigidbody, or null for a static collider.</summary>
    public RigidbodyComponent? Rigidbody { get; } = rigidbody;

    /// <summary>The colliders forming the body.</summary>
    public List<ColliderComponent> Colliders { get; set; } = [];

    /// <summary>The backend body, invalid until built.</summary>
    public BodyHandle Handle { get; set; }

    /// <summary>The shapes created for the colliders.</summary>
    public List<ShapeHandle> Shapes { get; } = [];

    /// <summary>The body's motion when built.</summary>
    public MotionType Motion { get; set; }

    /// <summary>Whether the body must be rebuilt before the next step.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>The node's transform version last synchronized.</summary>
    public long Version { get; set; }

    /// <summary>Each collider's transform version and geometry when last synchronized.</summary>
    public List<(long Version, PhysicsPose Local, Vector3 Scale)> ColliderStates { get; } = [];

    /// <summary>The pose before the last step, for interpolation.</summary>
    public PhysicsPose Previous { get; set; }

    /// <summary>The pose after the last step.</summary>
    public PhysicsPose Current { get; set; }

    /// <summary>The pose of the node's origin.</summary>
    public PhysicsPose NodePose()
    {
        var global = Node.GlobalTransform;
        return new PhysicsPose(global.Position, global.Orientation);
    }

    /// <summary>Writes a pose to the node, keeping its scale, and records the resulting versions.</summary>
    public void WriteNodePose(in PhysicsPose pose)
    {
        Node.GlobalTransform = Node.GlobalTransform with { Position = pose.Position, Orientation = pose.Orientation };
        Version = Node.TransformVersion;
        for (var i = 0; i < Colliders.Count && i < ColliderStates.Count; i++)
        {
            ColliderStates[i] = ColliderStates[i] with { Version = Colliders[i].Node?.TransformVersion ?? 0 };
        }
    }

    /// <summary>A collider's pose relative to the body's origin and its node's global scale.</summary>
    public (PhysicsPose Local, Vector3 Scale) Geometry(ColliderComponent collider, in PhysicsPose origin)
    {
        var pose = collider.WorldPose(out _);
        return (origin.Inverse().Multiply(pose), collider.Node?.GlobalTransform.Scale ?? Vector3.One);
    }

    /// <summary>Whether a collider moved relative to the body or was rescaled since the body was built.</summary>
    public bool GeometryChanged(int index, in PhysicsPose origin)
    {
        var collider = Colliders[index];
        var state = ColliderStates[index];
        var version = collider.Node?.TransformVersion ?? 0;
        if (version == state.Version)
        {
            return false;
        }

        var (local, scale) = Geometry(collider, origin);
        ColliderStates[index] = state with { Version = version };
        return !Near(local.Position, state.Local.Position) || !Near(scale, state.Scale) ||
               MathF.Abs(Quaternion.Dot(local.Orientation, state.Local.Orientation)) < 1f - Tolerance;
    }

    static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= Tolerance * Tolerance;
}
