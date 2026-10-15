namespace Turian.Engine.Physics;

public sealed partial class PhysicsScene
{
    readonly Dictionary<JointComponent, JointRecord> joints = [];

    /// <summary>The number of joints created in the backend.</summary>
    public int JointCount => joints.Values.Count(static record => record.Handle.IsValid);

    /// <summary>Whether the backend holds the joint, which waits until its bodies exist.</summary>
    public bool IsCreated(JointComponent joint) => joints.TryGetValue(joint, out var record) && record.Handle.IsValid;

    /// <summary>Adds an enabled joint at the next step, once its bodies exist.</summary>
    public void Register(JointComponent joint)
    {
        ArgumentNullException.ThrowIfNull(joint);
        joints.TryAdd(joint, new JointRecord());
    }

    /// <summary>Removes a disabled joint.</summary>
    public void Unregister(JointComponent joint)
    {
        ArgumentNullException.ThrowIfNull(joint);
        if (joints.Remove(joint, out var record) && record.Handle.IsValid && World is { } world)
        {
            world.RemoveJoint(record.Handle);
        }
    }

    /// <summary>Recreates a joint at the next step after one of its properties changed.</summary>
    public void MarkDirty(JointComponent joint)
    {
        if (joints.TryGetValue(joint, out var record))
        {
            record.Dirty = true;
        }
    }

    // Joints follow their bodies: removing a body removes its joints, so a rebuilt body gets them back here.
    void SynchronizeJoints()
    {
        foreach (var (joint, record) in joints)
        {
            var a = joint.Body is { } body ? Built(body) : null;
            var b = joint.ConnectedBody is { } connected ? Built(connected) : null;
            var handles = (a?.Handle ?? default, b?.Handle ?? default);
            if (!record.Dirty && handles == record.Bodies)
            {
                continue;
            }

            if (record.Handle.IsValid)
            {
                World!.RemoveJoint(record.Handle);
            }

            record.Handle = default;
            record.Bodies = handles;
            record.Dirty = false;
            if (a is not null && (joint.ConnectedBody is null || b is not null) && a != b)
            {
                record.Handle = World!.CreateJoint(DescribeJoint(joint, a, b));
            }
        }
    }

    JointDesc DescribeJoint(JointComponent joint, PhysicsBody a, PhysicsBody? b)
    {
        var description = joint.Describe(out var worldAxis);
        var poseA = World!.GetPose(a.Handle);
        var poseB = b is null ? PhysicsPose.Identity : World.GetPose(b.Handle);
        var worldAnchor = Vector3.Transform(joint.Anchor, joint.Node!.GlobalTransform.Matrix4X4());
        if (joint.AutoConfigureConnectedAnchor)
        {
            joint.ConnectedAnchorAtCreation(poseB.Inverse().TransformPoint(worldAnchor));
        }

        var anchorB = joint.ConnectedAnchor;
        var inverseB = Quaternion.Conjugate(poseB.Orientation);
        return Resolved(description with
        {
            BodyA = a.Handle,
            BodyB = b?.Handle ?? default,
            AnchorA = poseA.Inverse().TransformPoint(worldAnchor),
            AnchorB = anchorB,
            AxisA = Vector3.Transform(worldAxis, Quaternion.Conjugate(poseA.Orientation)),
            AxisB = Vector3.Transform(worldAxis, inverseB),
            RelativeOrientation = Quaternion.Normalize(Quaternion.Conjugate(poseA.Orientation) * poseB.Orientation),
            EnableCollision = joint.EnableCollision,
        }, Vector3.Distance(worldAnchor, poseB.TransformPoint(anchorB)));
    }

    // Distances left negative mean "as placed": the anchors' distance when the joint is created.
    static JointDesc Resolved(in JointDesc description, float distance) => description.Type switch
    {
        JointType.Distance when description.Limit.Max < 0f =>
            description with { Limit = description.Limit with { Max = MathF.Max(distance, description.Limit.Min) } },
        JointType.Spring when description.Spring.Target < 0f =>
            description with { Spring = description.Spring with { Target = distance } },
        _ => description,
    };

    /// <summary>A joint's backend handle and the bodies it was created between.</summary>
    sealed class JointRecord
    {
        public JointHandle Handle { get; set; }

        public (BodyHandle A, BodyHandle B) Bodies { get; set; }

        public bool Dirty { get; set; } = true;
    }
}
