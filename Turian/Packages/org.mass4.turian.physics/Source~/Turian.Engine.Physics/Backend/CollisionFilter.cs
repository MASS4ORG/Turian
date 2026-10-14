namespace Turian.Engine.Physics;

/// <summary>
/// Which collider categories may touch, as in Unity.Physics: two colliders collide when each one's
/// <see cref="BelongsTo"/> meets the other's <see cref="CollidesWith"/>. A shared non-zero group overrides the masks.
/// </summary>
/// <param name="BelongsTo">The categories, usually one bit per physics layer, the collider is in.</param>
/// <param name="CollidesWith">The categories the collider accepts.</param>
/// <param name="GroupIndex">A shared positive group always collides; a shared negative group never does.</param>
public readonly record struct CollisionFilter(uint BelongsTo, uint CollidesWith, int GroupIndex = 0)
{
    /// <summary>A filter that belongs to and collides with everything.</summary>
    public static CollisionFilter Default { get; } = new(uint.MaxValue, uint.MaxValue);

    /// <summary>A filter that never collides.</summary>
    public static CollisionFilter Zero { get; } = new(0, 0);

    /// <summary>Whether two collider filters allow contact.</summary>
    public static bool IsCollisionEnabled(in CollisionFilter a, in CollisionFilter b)
    {
        if (a.GroupIndex == b.GroupIndex && a.GroupIndex != 0)
        {
            return a.GroupIndex > 0;
        }

        return (a.BelongsTo & b.CollidesWith) != 0 && (b.BelongsTo & a.CollidesWith) != 0;
    }

    /// <summary>Whether a query with this filter accepts a collider; queries ignore the collider's own mask.</summary>
    public bool Accepts(in CollisionFilter collider) => (CollidesWith & collider.BelongsTo) != 0;
}
