namespace Turian.Engine.Physics;

/// <summary>When the physics world advances.</summary>
public enum SimulationMode
{
    /// <summary>Once per fixed tick, after the components' fixed updates.</summary>
    FixedUpdate,

    /// <summary>Once per frame with the frame's simulation time, before the components' updates.</summary>
    Update,

    /// <summary>Only when a script calls <see cref="PhysicsScene.Simulate"/>.</summary>
    Script,
}

/// <summary>Project-wide 3D physics configuration.</summary>
[CreateAssetMenu(fileName: "PhysicsSettings", path: "Settings/Physics")]
[TypeId("ebb1b487-2390-4a90-b798-3709d1932dd2")]
public sealed class PhysicsSettings : ProjectSettingsAsset
{
    /// <summary>The acceleration applied to bodies that use gravity.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>Velocity iterations per substep; more makes stacks and joints stiffer.</summary>
    public int SolverIterations { get; set; } = 4;

    /// <summary>Solver substeps per step; more improves stability at a proportional cost.</summary>
    public int Substeps { get; set; } = 4;

    /// <summary>When the world advances.</summary>
    public SimulationMode SimulationMode { get; set; }

    /// <summary>The material of colliders without one; null uses 0.6 friction and no bounce.</summary>
    public PhysicsMaterial? DefaultMaterial { get; set; }

    /// <summary>For each physics layer, the layers it collides with; missing entries collide with everything.</summary>
    public List<uint> LayerCollisionMatrix { get; set; } = [.. Enumerable.Repeat(uint.MaxValue, 32)];

    /// <summary>Whether queries report trigger colliders unless a query says otherwise.</summary>
    public bool QueriesHitTriggers { get; set; } = true;

    /// <summary>The speed in meters per second below which bodies may fall asleep.</summary>
    public float SleepThreshold { get; set; } = 0.1f;

    /// <summary>Spreads each step over worker threads.</summary>
    public bool Multithreaded { get; set; } = true;

    /// <summary>Repeats results for the same inputs on the same machine, at some cost; best effort only.</summary>
    public bool Deterministic { get; set; }

    /// <summary>The layers a layer collides with.</summary>
    public uint CollisionMask(int layer) =>
        (uint)layer < (uint)LayerCollisionMatrix.Count ? LayerCollisionMatrix[layer] : uint.MaxValue;

    /// <summary>Whether two physics layers collide at the start of a session.</summary>
    public bool GetLayerCollision(int layerA, int layerB)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerA, 31u, nameof(layerA));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerB, 31u, nameof(layerB));
        return (CollisionMask(layerA) & (1u << layerB)) != 0;
    }

    /// <summary>Makes two physics layers collide or not at the start of a session, in both directions.</summary>
    public void SetLayerCollision(int layerA, int layerB, bool collide)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerA, 31u, nameof(layerA));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)layerB, 31u, nameof(layerB));
        while (LayerCollisionMatrix.Count < 32)
        {
            LayerCollisionMatrix.Add(uint.MaxValue);
        }

        SetBit(layerA, layerB, collide);
        SetBit(layerB, layerA, collide);
    }

    void SetBit(int layer, int other, bool collide) => LayerCollisionMatrix[layer] = collide
        ? LayerCollisionMatrix[layer] | (1u << other)
        : LayerCollisionMatrix[layer] & ~(1u << other);

    /// <summary>The backend settings these describe.</summary>
    public PhysicsWorldSettings ToWorldSettings() => new()
    {
        Gravity = Gravity,
        SolverIterations = SolverIterations,
        Substeps = Substeps,
        Multithreaded = Multithreaded,
        Deterministic = Deterministic,
    };
}
