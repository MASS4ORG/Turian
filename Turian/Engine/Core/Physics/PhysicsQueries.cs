namespace Turian.Engine.Core;

/// <summary>Applies project physics layers consistently to every backend query; absent backends return misses.</summary>
[InternalService(InternalServiceLifetime.Singleton, typeof(IPhysicsQueries))]
public sealed class PhysicsQueries(LayerFilter layers, IPhysicsQueryBackend? backend = null) : IPhysicsQueries
{
    /// <inheritdoc />
    public PhysicsHit? Raycast(Ray ray, float maxDistance = float.PositiveInfinity, LayerMask? layerMask = null) =>
        Closest(RaycastAll(ray, maxDistance, layerMask));

    /// <inheritdoc />
    public IReadOnlyList<PhysicsHit> RaycastAll(Ray ray, float maxDistance = float.PositiveInfinity,
        LayerMask? layerMask = null) => Filter(backend?.RaycastAll(ray, maxDistance) ?? [], layerMask);

    /// <inheritdoc />
    public IReadOnlyList<Node> OverlapBox(Bounds bounds, LayerMask? layerMask = null) =>
        [.. (backend?.OverlapBox(bounds) ?? []).Where(node => Includes(node, layerMask)).Distinct()];

    /// <inheritdoc />
    public PhysicsHit? BoxCast(Bounds bounds, Vector3 direction, float maxDistance = float.PositiveInfinity,
        LayerMask? layerMask = null) =>
        Closest(Filter(backend?.BoxCastAll(bounds, direction, maxDistance) ?? [], layerMask));

    IReadOnlyList<PhysicsHit> Filter(IEnumerable<PhysicsHit> hits, LayerMask? mask) =>
        [.. hits.Where(hit => Includes(hit.Node, mask)).OrderBy(hit => hit.Distance)];

    bool Includes(Node node, LayerMask? mask) =>
        node.IsActiveInHierarchy && layers.IncludesPhysics(node, mask ?? LayerMask.Everything);

    static PhysicsHit? Closest(IReadOnlyList<PhysicsHit> hits) => hits.Count == 0 ? null : hits[0];
}
