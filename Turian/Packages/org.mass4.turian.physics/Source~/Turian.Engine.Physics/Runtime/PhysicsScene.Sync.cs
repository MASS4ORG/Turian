namespace Turian.Engine.Physics;

public sealed partial class PhysicsScene
{
    readonly List<PhysicsBody> records = [];
    readonly Dictionary<RigidbodyComponent, PhysicsBody> bodies = [];
    readonly Dictionary<ColliderComponent, PhysicsBody> owners = [];
    readonly Dictionary<int, PhysicsBody> byHandle = [];

    /// <summary>The number of backend bodies built for the scene's rigidbodies and static colliders.</summary>
    public int BodyCount => byHandle.Count;

    internal PhysicsPose? GetPose(RigidbodyComponent body) =>
        Built(body) is { } record ? World!.GetPose(record.Handle) : null;

    internal PhysicsVelocity? GetVelocity(RigidbodyComponent body) =>
        Built(body) is { } record ? World!.GetVelocity(record.Handle) : null;

    internal void SetVelocity(RigidbodyComponent body, in PhysicsVelocity velocity)
    {
        if (Built(body) is { } record)
        {
            World!.SetVelocity(record.Handle, velocity);
        }
    }

    internal Vector3? GetCenterOfMass(RigidbodyComponent body) =>
        Built(body) is { } record ? World!.GetCenterOfMass(record.Handle) : null;

    internal void Sleep(RigidbodyComponent body)
    {
        if (Built(body) is { } record)
        {
            World!.Sleep(record.Handle);
        }
    }

    internal void WakeUp(RigidbodyComponent body)
    {
        if (Built(body) is { } record)
        {
            World!.Wake(record.Handle);
        }
    }

    internal bool IsSleeping(RigidbodyComponent body) =>
        Built(body) is not { } record || !World!.IsAwake(record.Handle);

    internal BodyHandle HandleOf(RigidbodyComponent body) => Built(body)?.Handle ?? default;

    internal PhysicsBody? RecordOf(BodyHandle handle) => byHandle.GetValueOrDefault(handle.Id);

    PhysicsBody? Built(RigidbodyComponent body) =>
        World is not null && bodies.TryGetValue(body, out var record) && record.Handle.IsValid ? record : null;

    // Brings the backend up to date with the scene before a step.
    void Synchronize(float deltaTime)
    {
        if (assignmentDirty)
        {
            Assign();
        }

        foreach (var record in records)
        {
            if (record.Dirty)
            {
                Build(record);
            }
            else
            {
                PushTransform(record);
            }

            ApplyCommands(record, deltaTime);
        }
    }

    // Groups colliders under their nearest rigidbody; the others become one static body each.
    void Assign()
    {
        assignmentDirty = false;
        var (groups, statics) = GroupColliders();
        var kept = new List<PhysicsBody>(groups.Count + statics.Count);
        foreach (var (body, colliders) in groups)
        {
            var record = bodies.TryGetValue(body, out var existing) ? existing : new PhysicsBody(body.Node!, body);
            bodies[body] = record;
            Adopt(record, colliders, kept);
        }

        foreach (var collider in statics)
        {
            Adopt(StaticRecord(collider), [collider], kept);
        }

        RemoveStale(kept, groups);
        records.Clear();
        records.AddRange(kept);
        IndexOwners();
    }

    (Dictionary<RigidbodyComponent, List<ColliderComponent>> Groups, List<ColliderComponent> Statics) GroupColliders()
    {
        var groups = registeredBodies.ToDictionary(static body => body, static _ => new List<ColliderComponent>());
        var statics = new List<ColliderComponent>();
        foreach (var collider in registeredColliders)
        {
            if (FindRigidbody(collider.Node) is { } body && groups.TryGetValue(body, out var group))
            {
                group.Add(collider);
            }
            else
            {
                statics.Add(collider);
            }
        }

        return (groups, statics);
    }

    PhysicsBody StaticRecord(ColliderComponent collider) =>
        owners.TryGetValue(collider, out var existing) && existing.Rigidbody is null
            ? existing
            : new PhysicsBody(collider.Node!, null);

    void RemoveStale(List<PhysicsBody> kept, Dictionary<RigidbodyComponent, List<ColliderComponent>> groups)
    {
        foreach (var stale in records.Except(kept))
        {
            Destroy(stale);
            if (stale.Rigidbody is { } body && !groups.ContainsKey(body))
            {
                bodies.Remove(body);
            }
        }
    }

    void IndexOwners()
    {
        owners.Clear();
        foreach (var record in records)
        {
            foreach (var collider in record.Colliders)
            {
                owners[collider] = record;
            }
        }
    }

    static void Adopt(PhysicsBody record, List<ColliderComponent> colliders, List<PhysicsBody> kept)
    {
        if (!record.Colliders.SequenceEqual(colliders))
        {
            record.Colliders = colliders;
            record.Dirty = true;
        }

        kept.Add(record);
    }

    void Build(PhysicsBody record)
    {
        var world = World!;
        var velocity = record.Handle.IsValid
            ? world.GetVelocity(record.Handle)
            : record.Rigidbody?.DetachedVelocity ?? default;
        Destroy(record);
        record.Dirty = false;
        var origin = record.NodePose();
        var colliders = DescribeColliders(record, world, origin);
        if (record.Rigidbody is not { } rigidbody)
        {
            record.Motion = MotionType.Static;
            record.Handle = colliders.Length > 0 ? world.CreateStatic(new StaticDesc(origin, colliders)) : default;
        }
        else
        {
            record.Motion = rigidbody.IsKinematic ? MotionType.Kinematic : MotionType.Dynamic;
            record.Handle = world.CreateBody(DescribeBody(rigidbody, origin, velocity, colliders));
        }

        record.Version = record.Node.TransformVersion;
        record.Previous = record.Current = origin;
        if (record.Handle.IsValid)
        {
            byHandle[record.Handle.Id] = record;
        }
    }

    // Creates the colliders' shapes and records the geometry they were built from.
    ColliderDesc[] DescribeColliders(PhysicsBody record, IPhysicsWorld world, in PhysicsPose origin)
    {
        var colliders = new ColliderDesc[record.Colliders.Count];
        for (var i = 0; i < colliders.Length; i++)
        {
            var collider = record.Colliders[i];
            var (local, scale) = record.Geometry(collider, origin);
            collider.WorldPose(out var shape);
            var handle = world.CreateShape(shape);
            record.Shapes.Add(handle);
            record.ColliderStates.Add((collider.Node?.TransformVersion ?? 0, local, scale));
            colliders[i] = new ColliderDesc(handle, local, FilterOf(collider), MaterialOf(collider),
                collider.IsTrigger, collider.PhysicsId);
        }

        return colliders;
    }

    BodyDesc DescribeBody(RigidbodyComponent rigidbody, in PhysicsPose origin, in PhysicsVelocity velocity,
        ColliderDesc[] colliders) => new()
        {
            Motion = rigidbody.IsKinematic ? MotionType.Kinematic : MotionType.Dynamic,
            Pose = origin,
            Velocity = velocity,
            Colliders = colliders,
            Mass = new PhysicsMass(rigidbody.Mass, rigidbody.CenterOfMass, rigidbody.AutomaticCenterOfMass),
            LinearDamping = rigidbody.LinearDamping,
            AngularDamping = rigidbody.AngularDamping,
            GravityScale = rigidbody.UseGravity ? rigidbody.GravityScale : 0f,
            Continuous = rigidbody.CollisionDetectionMode == CollisionDetectionMode.Continuous,
            Locks = (AxisLocks)rigidbody.Constraints,
            SleepThreshold = Settings.SleepThreshold,
        };

    void Destroy(PhysicsBody record)
    {
        if (World is { } world)
        {
            if (record.Handle.IsValid)
            {
                byHandle.Remove(record.Handle.Id);
                world.Remove(record.Handle);
            }

            foreach (var shape in record.Shapes)
            {
                world.RemoveShape(shape);
            }
        }

        record.Handle = default;
        record.Shapes.Clear();
        record.ColliderStates.Clear();
    }

    // A script moved the node: teleport the body, or rebuild it when a collider moved within it or was rescaled.
    void PushTransform(PhysicsBody record)
    {
        if (!record.Handle.IsValid)
        {
            return;
        }

        var origin = record.NodePose();
        for (var i = 0; i < record.Colliders.Count; i++)
        {
            if (record.GeometryChanged(i, origin))
            {
                Build(record);
                return;
            }
        }

        var version = record.Node.TransformVersion;
        if (version != record.Version)
        {
            World!.SetPose(record.Handle, origin);
            record.Previous = record.Current = origin;
        }

        record.Version = version;
    }

    void ApplyCommands(PhysicsBody record, float deltaTime)
    {
        if (record.Rigidbody is not { } rigidbody || !record.Handle.IsValid)
        {
            return;
        }

        var commands = rigidbody.Commands;
        if (commands.TargetPosition is not null || commands.TargetRotation is not null)
        {
            ApplyMove(record, commands, deltaTime);
        }

        if (commands.HasForces && record.Motion == MotionType.Dynamic)
        {
            ApplyForces(record.Handle, commands, deltaTime);
        }

        commands.ClearForces();
    }

    // Kinematic bodies reach the target over the tick so they push; the others teleport.
    void ApplyMove(PhysicsBody record, RigidbodyCommands commands, float deltaTime)
    {
        var world = World!;
        var current = world.GetPose(record.Handle);
        var target = new PhysicsPose(commands.TargetPosition ?? current.Position,
            commands.TargetRotation ?? current.Orientation);
        if (record.Motion == MotionType.Kinematic)
        {
            world.SetKinematicTarget(record.Handle, target, deltaTime);
        }
        else
        {
            world.SetPose(record.Handle, target);
        }

        commands.TargetPosition = null;
        commands.TargetRotation = null;
    }

    void ApplyForces(BodyHandle handle, RigidbodyCommands commands, float deltaTime)
    {
        var world = World!;
        var center = world.GetCenterOfMass(handle);
        var linear = commands.Force * deltaTime + commands.Impulse;
        var angular = commands.Torque * deltaTime + commands.AngularImpulse;
        foreach (var (force, point, continuous) in commands.PointForces)
        {
            var impulse = continuous ? force * deltaTime : force;
            linear += impulse;
            angular += Vector3.Cross(point - center, impulse);
        }

        world.ApplyImpulse(handle, linear, center);
        world.ApplyAngularImpulse(handle, angular);
        var spin = commands.AngularAcceleration * deltaTime + commands.AngularVelocityChange;
        if (spin != Vector3.Zero)
        {
            var velocity = world.GetVelocity(handle);
            world.SetVelocity(handle, velocity with { Angular = velocity.Angular + spin });
        }
    }

    // Moves the nodes of awake bodies to their simulated poses.
    void PullPoses()
    {
        foreach (var record in records)
        {
            if (record.Motion == MotionType.Static || !record.Handle.IsValid)
            {
                continue;
            }

            record.Previous = record.Current;
            if (!World!.IsAwake(record.Handle))
            {
                continue;
            }

            record.Current = World.GetPose(record.Handle);
            record.WriteNodePose(record.Current);
        }
    }

    void Interpolate(float alpha)
    {
        foreach (var record in records)
        {
            if (record.Rigidbody is not { Interpolation: RigidbodyInterpolation.Interpolate } ||
                !record.Handle.IsValid || record.Motion == MotionType.Static)
            {
                continue;
            }

            var pose = new PhysicsPose(Vector3.Lerp(record.Previous.Position, record.Current.Position, alpha),
                Quaternion.Slerp(record.Previous.Orientation, record.Current.Orientation, alpha));
            record.WriteNodePose(pose);
        }
    }

    CollisionFilter FilterOf(ColliderComponent collider)
    {
        var layer = LayerOf(collider.Node);
        var accepts = (layerMatrix[layer] | collider.IncludeLayers.Value) & ~collider.ExcludeLayers.Value;
        return new CollisionFilter(1u << layer, accepts);
    }

    MaterialDesc MaterialOf(ColliderComponent collider) =>
        collider.Material?.ToDesc() ?? Settings.DefaultMaterial?.ToDesc() ?? MaterialDesc.Default;
}
