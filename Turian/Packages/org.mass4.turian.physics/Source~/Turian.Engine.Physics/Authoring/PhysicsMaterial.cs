namespace Turian.Engine.Physics;

/// <summary>Friction and bounciness of a collider's surface, shared between colliders as a data asset.</summary>
[CreateAssetMenu(fileName: "PhysicsMaterial", path: "Physics/Physics Material")]
[TypeId("eff5c528-253a-4979-b3b4-7c8819d9722a")]
public sealed class PhysicsMaterial : DataAsset
{
    /// <summary>Friction while sliding, usually in [0, 1].</summary>
    public float DynamicFriction { get; set; } = 0.6f;

    /// <summary>Friction while at rest, usually in [0, 1].</summary>
    public float StaticFriction { get; set; } = 0.6f;

    /// <summary>0 absorbs impacts, 1 keeps their energy. Approximated with contact springs.</summary>
    public float Bounciness { get; set; }

    /// <summary>How friction combines with the other collider's.</summary>
    public PhysicsMaterialCombine FrictionCombine { get; set; }

    /// <summary>How bounciness combines with the other collider's.</summary>
    public PhysicsMaterialCombine BounceCombine { get; set; }

    /// <summary>The backend description of this material.</summary>
    public MaterialDesc ToDesc() =>
        new(StaticFriction, DynamicFriction, Bounciness, FrictionCombine, BounceCombine);
}
