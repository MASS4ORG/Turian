namespace Turian.Engine.Physics;

public sealed partial class BepuPhysicsWorld
{
    /// <summary>Whether a body is static or asleep, so its pairs are not retested; false for removed bodies.</summary>
    internal bool IsDormant(int id) => records.TryGetValue(id, out var record) &&
                                       (record.Motion == MotionType.Static || !Simulation.Bodies[record.Body].Awake);

    /// <summary>The world position of a body's collidable.</summary>
    internal Vector3 CollidablePosition(BepuBody body) => CollidablePose(body).Position;

    /// <summary>The world pose of a body's collidable.</summary>
    internal RigidPose CollidablePose(BepuBody body) => body.Motion == MotionType.Static
        ? Simulation.Statics[body.Static].Pose
        : Simulation.Bodies[body.Body].Pose;

    /// <summary>The velocity of a body at a world point; zero for statics.</summary>
    internal Vector3 PointVelocity(BepuBody body, Vector3 point)
    {
        if (body.Motion == MotionType.Static)
        {
            return Vector3.Zero;
        }

        var reference = Simulation.Bodies[body.Body];
        var velocity = reference.Velocity;
        return velocity.Linear + Vector3.Cross(velocity.Angular, point - reference.Pose.Position);
    }

    /// <summary>The inverse mass of a body; zero for statics and kinematics.</summary>
    internal float InverseMass(BepuBody body) => body.Motion == MotionType.Dynamic
        ? Simulation.Bodies[body.Body].LocalInertia.InverseMass
        : 0f;

    /// <summary>The world pose of one collider of a body.</summary>
    internal RigidPose ColliderPose(BepuBody body, int collider)
    {
        var origin = body.ToOrigin(CollidablePose(body));
        var shape = shapes[body.Colliders[collider].Shape.Id];
        var local = ChildPose(body.Colliders[collider], shape);
        return new RigidPose(origin.TransformPoint(local.Position),
            Quaternion.Normalize(origin.Orientation * local.Orientation));
    }

    /// <summary>The Bepu shape of one collider.</summary>
    internal TypedIndex ColliderShape(BepuBody body, int collider) => shapes[body.Colliders[collider].Shape.Id].Index;
}
