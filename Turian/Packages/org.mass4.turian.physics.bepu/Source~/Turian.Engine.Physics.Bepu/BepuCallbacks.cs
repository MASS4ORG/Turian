namespace Turian.Engine.Physics;

/// <summary>Filters pairs, combines materials and records contacts and trigger overlaps on the worker threads.</summary>
struct BepuNarrowPhaseCallbacks(BepuPhysicsWorld world) : INarrowPhaseCallbacks
{
    // Bounciness drives the contact spring towards an undamped one that keeps more of the impact speed.
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

        pairMaterial = Combine(bodyA.Material, bodyB.Material, world.ContactFrequency);
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

    static PairMaterialProperties Combine(in MaterialDesc a, in MaterialDesc b, float frequency)
    {
        var friction = MaterialDesc.Combine(a.DynamicFriction, b.DynamicFriction, a.FrictionCombine, b.FrictionCombine);
        var bounce = Bounce(a, b);
        return new PairMaterialProperties(MathF.Max(friction, 0f),
            SolidRecoveryVelocity + bounce * (BouncyRecoveryVelocity - SolidRecoveryVelocity),
            new SpringSettings(frequency, 1f - bounce));
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
        Span<float> spinX = stackalloc float[Vector<float>.Count];
        Span<float> spinY = stackalloc float[Vector<float>.Count];
        Span<float> spinZ = stackalloc float[Vector<float>.Count];
        var handles = world.Simulation.Bodies.ActiveSet.IndexToHandle;
        for (var lane = 0; lane < Vector<float>.Count; lane++)
        {
            var index = bodyIndices[lane];
            var factors = LaneFactors.Of(index >= 0 ? world.FindBody(handles[index]) : null, dt[lane]);
            scale[lane] = factors.GravityScale;
            linear[lane] = factors.LinearDamping;
            angular[lane] = factors.AngularDamping;
            keepX[lane] = factors.Linear.X;
            keepY[lane] = factors.Linear.Y;
            keepZ[lane] = factors.Linear.Z;
            spinX[lane] = factors.Angular.X;
            spinY[lane] = factors.Angular.Y;
            spinZ[lane] = factors.Angular.Z;
        }

        var gravityStep = new Vector<float>(scale) * dt;
        var linearDamping = new Vector<float>(linear);
        var angularDamping = new Vector<float>(angular);
        velocity.Linear.X = (velocity.Linear.X + gravity.X * gravityStep) * linearDamping * new Vector<float>(keepX);
        velocity.Linear.Y = (velocity.Linear.Y + gravity.Y * gravityStep) * linearDamping * new Vector<float>(keepY);
        velocity.Linear.Z = (velocity.Linear.Z + gravity.Z * gravityStep) * linearDamping * new Vector<float>(keepZ);
        // Locked inertia stops forces from turning the body; the velocity a script set is dropped in local space.
        var local = QuaternionWide.TransformByConjugate(velocity.Angular * angularDamping, orientation);
        local.X *= new Vector<float>(spinX);
        local.Y *= new Vector<float>(spinY);
        local.Z *= new Vector<float>(spinZ);
        velocity.Angular = QuaternionWide.Transform(local, orientation);
    }

    /// <summary>One body's integration multipliers: 1 keeps an axis's velocity, 0 drops it.</summary>
    readonly record struct LaneFactors(float GravityScale, float LinearDamping, float AngularDamping, Vector3 Linear,
        Vector3 Angular)
    {
        public static LaneFactors Of(BepuBody? body, float step) => body is null
            ? new LaneFactors(0f, 1f, 1f, Vector3.One, Vector3.One)
            : new LaneFactors(body.GravityScale, MathF.Max(0f, 1f - body.LinearDamping * step),
                MathF.Max(0f, 1f - body.AngularDamping * step),
                Keep(body.Locks, AxisLocks.LinearX, AxisLocks.LinearY, AxisLocks.LinearZ),
                Keep(body.Locks, AxisLocks.AngularX, AxisLocks.AngularY, AxisLocks.AngularZ));

        static Vector3 Keep(AxisLocks locks, AxisLocks x, AxisLocks y, AxisLocks z) =>
            new(Free(locks, x), Free(locks, y), Free(locks, z));

        static float Free(AxisLocks locks, AxisLocks axis) => (locks & axis) == 0 ? 1f : 0f;
    }
}
