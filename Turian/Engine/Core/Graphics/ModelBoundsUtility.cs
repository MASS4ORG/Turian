namespace Turian.Engine.Core;

/// <summary>
/// Resolves a <see cref="ModelComponent"/>'s bounds. Bounds are baked at import time in local
/// (model) space only — never cached in world space at runtime — so anything that needs a node's
/// on-screen extent, such as headless scene reporting or viewport picking, transforms them itself.
/// </summary>
public static class ModelBoundsUtility
{
    /// <summary>
    /// Resolves a model component's bounds in its own local space.
    /// </summary>
    /// <param name="component">The component to measure.</param>
    /// <param name="loadModels">
    /// Whether to resolve <see cref="ModelComponent.ModelInstance"/> when the component references
    /// a whole model rather than a single mesh. Loading a model uploads its GPU resources, so
    /// callers that only need a mesh's precomputed bounds can pass <c>false</c> to skip that.
    /// </param>
    /// <param name="isResolved">
    /// <c>true</c> when the component's reference resolved to bounds (even if those bounds are
    /// empty because <paramref name="loadModels"/> was <c>false</c>); <c>false</c> when the
    /// reference is missing or could not be loaded.
    /// </param>
    /// <returns>The component's local-space bounds, or <see cref="Bounds.Empty"/> if unresolved.</returns>
    public static Bounds ComputeLocalBounds(ModelComponent component, bool loadModels, out bool isResolved)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (component.Mesh is { IsEmpty: false } meshReference)
        {
            var mesh = MeshAsset.Resolve(meshReference.AssetId);
            isResolved = mesh is not null && (!loadModels || component.ModelInstance is not null);
            return mesh?.Bounds ?? Bounds.Empty;
        }

        if (component.Model is not { IsEmpty: false })
        {
            isResolved = false;
            return Bounds.Empty;
        }

        if (!loadModels)
        {
            // Whole-model bounds live in the baked blob, which is only read when it is uploaded.
            isResolved = true;
            return Bounds.Empty;
        }

        var loaded = component.ModelInstance;
        isResolved = loaded is not null;

        return loaded is null
            ? Bounds.Empty
            : loaded.SubMeshes.Aggregate(Bounds.Empty, static (acc, sub) => acc.Encapsulate(sub.Bounds));
    }

    /// <summary>
    /// Transforms local-space bounds into world space by transforming all eight corners and
    /// re-encapsulating — cheaper alternatives (transforming just the centre and extents) do not
    /// hold once rotation is involved.
    /// </summary>
    /// <param name="local">Bounds in the source space.</param>
    /// <param name="transform">The transform from that space into world space.</param>
    public static Bounds ToWorldBounds(Bounds local, Matrix4x4 transform)
    {
        var world = Bounds.Empty;

        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z);

            world = world.Encapsulate(Vector3.Transform(point, transform));
        }

        return world;
    }
}
