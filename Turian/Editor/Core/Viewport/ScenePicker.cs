namespace Turian.Editor.Core;

/// <summary>
/// Resolves a viewport click to the scene node it hit, by testing a screen ray against each
/// <see cref="ModelComponent"/>'s world-space bounding box. Cheap enough to run once per click;
/// there is no per-frame cost and no cached world bounds to keep in sync.
/// </summary>
/// <remarks>
/// Bounds are recomputed on every call — there is no runtime bounds cache anywhere in the engine
/// today (only local-space bounds baked at import). Issue #134 (AABB bounds and frustum culling)
/// will want exactly this cache; once it lands, this should consume it instead of recomputing.
/// </remarks>
public static class ScenePicker
{
    /// <summary>
    /// Casts a ray from <paramref name="screenPos"/> through <paramref name="camera"/> and returns
    /// the node of the closest <see cref="ModelComponent"/> it hits.
    /// </summary>
    /// <param name="root">Root of the hierarchy to search. Inactive nodes are skipped.</param>
    /// <param name="camera">The camera the click was made through.</param>
    /// <param name="screenPos">Pointer position in viewport pixels.</param>
    /// <param name="viewportSize">Viewport size in pixels.</param>
    /// <returns>The hit node, or <c>null</c> when the ray hits nothing.</returns>
    public static Node? Pick(Node root, ICamera camera, Vector2 screenPos, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(camera);

        if (CameraMath.ScreenPointToRay(camera, screenPos, viewportSize) is not { } ray) return null;

        return PickClosest(EnumerateWorldBounds(root), ray);
    }

    /// <summary>
    /// Returns the node of the closest candidate <paramref name="ray"/> intersects. Separated from
    /// <see cref="Pick"/> so the closest-hit logic can be tested against known bounds without a
    /// GPU — resolving a <see cref="ModelComponent"/>'s bounds needs one (see
    /// <see cref="ModelComponent.ModelInstance"/>), the distance comparison does not.
    /// </summary>
    /// <param name="candidates">Nodes paired with their world-space bounding boxes.</param>
    /// <param name="ray">The ray to test against each box.</param>
    public static Node? PickClosest(IEnumerable<(Node Node, Bounds WorldBounds)> candidates, Ray ray)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        Node? closestNode = null;
        var closestDistance = float.PositiveInfinity;

        foreach (var (node, bounds) in candidates)
        {
            if (!bounds.TryIntersect(ray, out var distance)) continue;

            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestNode = node;
            }
        }

        return closestNode;
    }

    static IEnumerable<(Node Node, Bounds WorldBounds)> EnumerateWorldBounds(Node root)
    {
        foreach (var component in Node.GetComponentsInChildren<ModelComponent>(root))
        {
            var local = ModelBoundsUtility.ComputeLocalBounds(component, loadModels: true, out var isResolved);
            if (!isResolved || local.IsEmpty) continue;

            if (component.Node is not { } node) continue;
            yield return (node, ModelBoundsUtility.ToWorldBounds(local, node.GlobalTransform.Matrix4X4()));
        }
    }
}
