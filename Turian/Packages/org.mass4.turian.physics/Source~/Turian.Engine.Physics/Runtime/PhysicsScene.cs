namespace Turian.Engine.Physics;

/// <summary>
/// The 3D physics of one running game session: owns the backend world, keeps it in step with the scene's
/// colliders and rigidbodies, dispatches collision callbacks and answers Unity-style queries. Without a backend
/// brick it simulates nothing and every query misses.
/// </summary>
/// <remarks>Used from the game loop thread only.</remarks>
public sealed partial class PhysicsScene : IFixedTickSystem, IDisposable
{
    readonly LayerFilter? layers;
    readonly SimulationClock? clock;
    readonly List<ColliderComponent> registeredColliders = [];
    readonly List<RigidbodyComponent> registeredBodies = [];
    readonly Dictionary<ulong, ColliderComponent> collidersById = [];
    readonly uint[] layerMatrix = new uint[32];
    double lastFrameTime;
    bool assignmentDirty;
    bool disposed;

    /// <summary>Creates the session's physics from the project's <see cref="PhysicsSettings"/>.</summary>
    /// <param name="settings">The project settings; null uses defaults.</param>
    /// <param name="layers">Resolves nodes' physics layers; null uses their raw index.</param>
    /// <param name="clock">The session clock, used in <see cref="SimulationMode.Update"/> mode.</param>
    /// <param name="factory">The backend; null finds one among the loaded bricks.</param>
    /// <param name="loader">Resolves the settings' material references, which settings read from sources leave pending.</param>
    public PhysicsScene(IAppSettings? settings = null, LayerFilter? layers = null, SimulationClock? clock = null,
        IPhysicsWorldFactory? factory = null, IAssetLoader? loader = null)
    {
        this.layers = layers;
        this.clock = clock;
        Settings = settings?.Get<PhysicsSettings>() ?? new PhysicsSettings();
        if (loader is not null)
        {
            ObjectReferences.ResolveAsync(Settings, loader).GetAwaiter().GetResult();
        }

        for (var layer = 0; layer < layerMatrix.Length; layer++)
        {
            layerMatrix[layer] = Settings.CollisionMask(layer);
        }

        World = (factory ?? PhysicsWorldFactories.Find())?.Create(Settings.ToWorldSettings());
        lastFrameTime = FrameTime();
    }

    /// <summary>The settings captured when the session started.</summary>
    public PhysicsSettings Settings { get; }

    /// <summary>The backend world, or null when no backend brick is loaded.</summary>
    public IPhysicsWorld? World { get; private set; }

    /// <summary>Whether a backend simulates this scene.</summary>
    public bool IsSimulating => World is not null;

    /// <summary>The acceleration applied to bodies that use gravity.</summary>
    public Vector3 Gravity
    {
        get => World?.Gravity ?? Settings.Gravity;
        set
        {
            if (World is not null)
            {
                World.Gravity = value;
            }
        }
    }

    /// <summary>The nearest enabled rigidbody on a node or its ancestors.</summary>
    public static RigidbodyComponent? FindRigidbody(Node? node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            foreach (var component in current.Components)
            {
                if (component is RigidbodyComponent { IsActive: true } body)
                {
                    return body;
                }
            }
        }

        return null;
    }

    /// <summary>The registered collider with a backend id, as hits and events report it.</summary>
    internal ColliderComponent? ColliderOf(ulong physicsId) => collidersById.GetValueOrDefault(physicsId);

    /// <summary>Adds an enabled collider to the simulation at the next step.</summary>
    public void Register(ColliderComponent collider)
    {
        ArgumentNullException.ThrowIfNull(collider);
        if (!collidersById.TryAdd(collider.PhysicsId, collider))
        {
            return;
        }

        registeredColliders.Add(collider);
        assignmentDirty = true;
    }

    /// <summary>Removes a disabled collider at the next step.</summary>
    public void Unregister(ColliderComponent collider)
    {
        ArgumentNullException.ThrowIfNull(collider);
        if (collidersById.Remove(collider.PhysicsId))
        {
            registeredColliders.Remove(collider);
            assignmentDirty = true;
        }
    }

    /// <summary>Adds an enabled rigidbody to the simulation at the next step.</summary>
    public void Register(RigidbodyComponent body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!registeredBodies.Contains(body))
        {
            registeredBodies.Add(body);
            assignmentDirty = true;
        }
    }

    /// <summary>Removes a disabled rigidbody at the next step; its colliders become static.</summary>
    public void Unregister(RigidbodyComponent body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (registeredBodies.Remove(body))
        {
            assignmentDirty = true;
        }
    }

    /// <summary>Rebuilds a collider's body at the next step after one of its properties changed.</summary>
    public void MarkDirty(ColliderComponent collider)
    {
        ArgumentNullException.ThrowIfNull(collider);
        if (owners.TryGetValue(collider, out var record))
        {
            record.Dirty = true;
        }

        // A collider gaining or losing its shape, such as a mesh collider's mesh, joins or leaves the bodies.
        var built = record is not null;
        if (built != collider.HasShape && collidersById.ContainsKey(collider.PhysicsId))
        {
            assignmentDirty = true;
        }
    }

    /// <summary>Rebuilds a rigidbody at the next step after one of its properties changed.</summary>
    public void MarkDirty(RigidbodyComponent body)
    {
        if (bodies.TryGetValue(body, out var record))
        {
            record.Dirty = true;
        }
    }

    /// <summary>Stops or restores contact between two colliders.</summary>
    public void IgnoreCollision(ColliderComponent a, ColliderComponent b, bool ignore = true)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        World?.SetPairIgnored(a.PhysicsId, b.PhysicsId, ignore);
    }

    /// <summary>Stops or restores contact between two physics layers for this session.</summary>
    public void IgnoreLayerCollision(int layerA, int layerB, bool ignore = true)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerA, 31u, nameof(layerA));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerB, 31u, nameof(layerB));
        SetLayerBit(layerA, layerB, !ignore);
        SetLayerBit(layerB, layerA, !ignore);
        foreach (var record in records)
        {
            record.Dirty = true;
        }
    }

    /// <summary>Whether two physics layers ignore each other.</summary>
    public bool GetIgnoreLayerCollision(int layerA, int layerB) =>
        (uint)layerA < 32 && (uint)layerB < 32 && (layerMatrix[layerA] & (1u << layerB)) == 0;

    /// <summary>Advances the simulation by <paramref name="deltaTime"/> seconds and dispatches its callbacks.</summary>
    public void Simulate(float deltaTime)
    {
        if (World is null || disposed || deltaTime <= 0f)
        {
            return;
        }

        Synchronize(deltaTime);
        World.Step(deltaTime);
        PullPoses();
        DispatchEvents();
    }

    /// <inheritdoc />
    public void FixedTick(long tick, double deltaTime)
    {
        if (Settings.SimulationMode == SimulationMode.FixedUpdate)
        {
            Simulate((float)deltaTime);
        }
    }

    /// <inheritdoc />
    public void AfterFixedTicks(double alpha)
    {
        var now = FrameTime();
        var frame = now - lastFrameTime;
        lastFrameTime = now;
        if (Settings.SimulationMode == SimulationMode.Update)
        {
            Simulate((float)frame);
        }
        else
        {
            Interpolate((float)alpha);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        World?.Dispose();
        World = null;
        records.Clear();
        bodies.Clear();
        owners.Clear();
        byHandle.Clear();
        joints.Clear();
    }

    double FrameTime() => clock is null ? 0d : clock.ElapsedSeconds + clock.AccumulatorSeconds;

    void SetLayerBit(int layer, int other, bool collide) =>
        layerMatrix[layer] = collide ? layerMatrix[layer] | (1u << other) : layerMatrix[layer] & ~(1u << other);

    int LayerOf(Node? node)
    {
        var raw = node?.PhysicsLayer ?? 0;
        return layers?.ResolvePhysics(raw).Index ?? Math.Clamp(raw, 0, 31);
    }
}
