namespace Turian.Engine.Physics;

/// <summary>Filters pairs, combines materials and records contacts and trigger overlaps on the worker threads.</summary>
struct BepuNarrowPhaseCallbacks(BepuPhysicsWorld world) : INarrowPhaseCallbacks
{
    // Bounciness drives the contact spring towards an undamped one that keeps more of the impact speed.
    const float ContactFrequency = 30f;
    const float SolidRecoveryVelocity = 2f;
    const float BouncyRecoveryVelocity = 100f;

    public readonly void Initialize(Simulation simulation)
    {
    }

    public readonly bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
        ref float speculativeMargin)
    {
        if (world.Find(a) is not { } bodyA || world.Find(b) is not { } bodyB)
        {
            return false;
        }

        var anyDynamic = a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
        if (!anyDynamic && !bodyA.AnyTrigger && !bodyB.AnyTrigger)
        {
            return false;
        }

        if (world.AreJointedWithoutCollision(bodyA, bodyB))
        {
            return false;
        }

        // Speculative contacts would stop a bouncy body before its contact spring compresses, so it never rebounds.
        if (Bounce(bodyA.Material, bodyB.Material) > 0.01f)
        {
            speculativeMargin = 0f;
        }

        if (!bodyA.IsCompound && !bodyB.IsCompound)
        {
            return ChildrenMayTouch(bodyA, bodyA.ColliderIndex(0), bodyB, bodyB.ColliderIndex(0));
        }

        return bodyA.UsesGroups || bodyB.UsesGroups ||
               ((bodyA.BelongsTo & bodyB.CollidesWith) != 0 && (bodyB.BelongsTo & bodyA.CollidesWith) != 0);
    }

    public readonly bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        if (world.Find(pair.A) is not { } bodyA || world.Find(pair.B) is not { } bodyB)
        {
            return false;
        }

        return ChildrenMayTouch(bodyA, bodyA.ColliderIndex(childIndexA), bodyB, bodyB.ColliderIndex(childIndexB));
    }

    public readonly bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair,
        ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial = default;
        if (world.Find(pair.A) is not { } bodyA || world.Find(pair.B) is not { } bodyB)
        {
            return false;
        }

        pairMaterial = Combine(bodyA.Material, bodyB.Material);
        var solidPossible = !bodyA.AllTriggers && !bodyB.AllTriggers &&
                            (pair.A.Mobility == CollidableMobility.Dynamic ||
                             pair.B.Mobility == CollidableMobility.Dynamic);
        if (bodyA.IsCompound || bodyB.IsCompound)
        {
            return solidPossible;
        }

        int colliderA = bodyA.ColliderIndex(0), colliderB = bodyB.ColliderIndex(0);
        if (colliderA < 0 || colliderB < 0)
        {
            return false;
        }

        var trigger = bodyA.Colliders[colliderA].IsTrigger || bodyB.Colliders[colliderB].IsTrigger;
        world.Events.Record(workerIndex, bodyA, colliderA, bodyB, colliderB, ref manifold, trigger);
        return solidPossible && !trigger;
    }

    public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA,
        int childIndexB, ref ConvexContactManifold manifold)
    {
        if (world.Find(pair.A) is not { } bodyA || world.Find(pair.B) is not { } bodyB)
        {
            return false;
        }

        // Meshes report triangles as children; only compounds map children to colliders here.
        if (!bodyA.IsCompound && !bodyB.IsCompound)
        {
            return true;
        }

        int colliderA = bodyA.ColliderIndex(childIndexA), colliderB = bodyB.ColliderIndex(childIndexB);
        if (colliderA < 0 || colliderB < 0)
        {
            return false;
        }

        var trigger = bodyA.Colliders[colliderA].IsTrigger || bodyB.Colliders[colliderB].IsTrigger;
        world.Events.Record(workerIndex, bodyA, colliderA, bodyB, colliderB, ref manifold, trigger);
        return !trigger;
    }

    public readonly void Dispose()
    {
    }

    readonly bool ChildrenMayTouch(BepuBody bodyA, int colliderA, BepuBody bodyB, int colliderB)
    {
        if (colliderA < 0 || colliderB < 0)
        {
            return false;
        }

        ref readonly var a = ref bodyA.Colliders[colliderA];
        ref readonly var b = ref bodyB.Colliders[colliderB];
        return CollisionFilter.IsCollisionEnabled(a.Filter, b.Filter) && !world.IsPairIgnored(a.UserData, b.UserData);
    }

    static PairMaterialProperties Combine(in MaterialDesc a, in MaterialDesc b)
    {
        var friction = MaterialDesc.Combine(a.DynamicFriction, b.DynamicFriction, a.FrictionCombine, b.FrictionCombine);
        var bounce = Bounce(a, b);
        return new PairMaterialProperties(MathF.Max(friction, 0f),
            SolidRecoveryVelocity + bounce * (BouncyRecoveryVelocity - SolidRecoveryVelocity),
            new SpringSettings(ContactFrequency, 1f - bounce));
    }

    static float Bounce(in MaterialDesc a, in MaterialDesc b) =>
        Math.Clamp(MaterialDesc.Combine(a.Bounciness, b.Bounciness, a.BounceCombine, b.BounceCombine), 0f, 1f);
}

/// <summary>Applies gravity, per-body damping and linear locks to SIMD bundles of bodies.</summary>
struct BepuPoseIntegratorCallbacks(BepuPhysicsWorld world) : IPoseIntegratorCallbacks
{
    Vector3Wide gravity;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    public readonly bool AllowSubstepsForUnconstrainedBodies => false;

    public readonly bool IntegrateVelocityForKinematics => false;

    public readonly void Initialize(Simulation simulation)
    {
    }

    public void PrepareForIntegration(float dt) => gravity = Vector3Wide.Broadcast(world.Gravity);

    public readonly void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        Span<float> scale = stackalloc float[Vector<float>.Count];
        Span<float> linear = stackalloc float[Vector<float>.Count];
        Span<float> angular = stackalloc float[Vector<float>.Count];
        Span<float> keepX = stackalloc float[Vector<float>.Count];
        Span<float> keepY = stackalloc float[Vector<float>.Count];
        Span<float> keepZ = stackalloc float[Vector<float>.Count];
        var handles = world.Simulation.Bodies.ActiveSet.IndexToHandle;
        for (var lane = 0; lane < Vector<float>.Count; lane++)
        {
            var index = bodyIndices[lane];
            var body = index >= 0 ? world.FindBody(handles[index]) : null;
            var step = dt[lane];
            scale[lane] = body?.GravityScale ?? 0f;
            linear[lane] = MathF.Max(0f, 1f - (body?.LinearDamping ?? 0f) * step);
            angular[lane] = MathF.Max(0f, 1f - (body?.AngularDamping ?? 0f) * step);
            var locks = body?.Locks ?? AxisLocks.None;
            keepX[lane] = (locks & AxisLocks.LinearX) != 0 ? 0f : 1f;
            keepY[lane] = (locks & AxisLocks.LinearY) != 0 ? 0f : 1f;
            keepZ[lane] = (locks & AxisLocks.LinearZ) != 0 ? 0f : 1f;
        }

        var gravityStep = new Vector<float>(scale) * dt;
        var linearDamping = new Vector<float>(linear);
        var angularDamping = new Vector<float>(angular);
        velocity.Linear.X = (velocity.Linear.X + gravity.X * gravityStep) * linearDamping * new Vector<float>(keepX);
        velocity.Linear.Y = (velocity.Linear.Y + gravity.Y * gravityStep) * linearDamping * new Vector<float>(keepY);
        velocity.Linear.Z = (velocity.Linear.Z + gravity.Z * gravityStep) * linearDamping * new Vector<float>(keepZ);
        velocity.Angular *= angularDamping;
    }
}
