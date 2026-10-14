namespace Turian.Engine.Physics;

/// <summary>How two colliders' friction or bounciness combine, as in Unity; the higher-ranked mode of the pair wins.</summary>
public enum PhysicsMaterialCombine
{
    /// <summary>The mean of both values; lowest rank.</summary>
    Average,

    /// <summary>The smaller value.</summary>
    Minimum,

    /// <summary>The product of both values.</summary>
    Multiply,

    /// <summary>The larger value; highest rank.</summary>
    Maximum,
}

/// <summary>Surface response of a collider.</summary>
/// <param name="StaticFriction">Friction while at rest, usually in [0, 1].</param>
/// <param name="DynamicFriction">Friction while sliding, usually in [0, 1].</param>
/// <param name="Bounciness">0 absorbs impacts, 1 keeps their energy.</param>
/// <param name="FrictionCombine">How the pair's friction combines.</param>
/// <param name="BounceCombine">How the pair's bounciness combines.</param>
public readonly record struct MaterialDesc(
    float StaticFriction,
    float DynamicFriction,
    float Bounciness,
    PhysicsMaterialCombine FrictionCombine = PhysicsMaterialCombine.Average,
    PhysicsMaterialCombine BounceCombine = PhysicsMaterialCombine.Average)
{
    /// <summary>Unity's default material: 0.6 friction and no bounce.</summary>
    public static MaterialDesc Default { get; } = new(0.6f, 0.6f, 0f);

    /// <summary>Combines a property of two materials using the higher-ranked mode of the pair.</summary>
    public static float Combine(float a, float b, PhysicsMaterialCombine modeA, PhysicsMaterialCombine modeB) =>
        (PhysicsMaterialCombine)Math.Max((int)modeA, (int)modeB) switch
        {
            PhysicsMaterialCombine.Minimum => MathF.Min(a, b),
            PhysicsMaterialCombine.Multiply => a * b,
            PhysicsMaterialCombine.Maximum => MathF.Max(a, b),
            _ => (a + b) * 0.5f,
        };
}
