namespace Turian.Engine.Core;

/// <summary>A physics query hit supplied by the installed physics backend.</summary>
/// <param name="Node">The node owning the hit shape.</param>
/// <param name="Point">The world position of the hit.</param>
/// <param name="Normal">The surface normal at the hit.</param>
/// <param name="Distance">Distance from the query origin.</param>
public readonly record struct PhysicsHit(Node Node, Vector3 Point, Vector3 Normal, float Distance);

/// <summary>Supplies geometric hits; the query service applies node activity and physics layer filtering.</summary>
public interface IPhysicsQueryBackend
{
    /// <summary>Returns all ray hits within the maximum distance, including every physics layer.</summary>
    IEnumerable<PhysicsHit> RaycastAll(Ray ray, float maxDistance);

    /// <summary>Returns the nodes whose physics shapes overlap a world-space box.</summary>
    IEnumerable<Node> OverlapBox(Bounds bounds);

    /// <summary>Returns hits from sweeping a world-space box along a direction.</summary>
    IEnumerable<PhysicsHit> BoxCastAll(Bounds bounds, Vector3 direction, float maxDistance);
}

/// <summary>Mask-aware physics queries independent of the installed geometric backend.</summary>
public interface IPhysicsQueries
{
    /// <summary>Returns the closest active hit accepted by the physics mask, or null on a miss.</summary>
    PhysicsHit? Raycast(Ray ray, float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null);

    /// <summary>Returns active ray hits accepted by the physics mask in increasing distance order.</summary>
    IReadOnlyList<PhysicsHit> RaycastAll(Ray ray, float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null);

    /// <summary>Returns active overlapping nodes accepted by the physics mask.</summary>
    IReadOnlyList<Node> OverlapBox(Bounds bounds, LayerMask? layerMask = null);

    /// <summary>Returns the closest active box sweep hit accepted by the physics mask, or null on a miss.</summary>
    PhysicsHit? BoxCast(Bounds bounds, Vector3 direction, float maxDistance = float.PositiveInfinity,
        LayerMask? layerMask = null);
}
