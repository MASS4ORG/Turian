namespace Turian.Editor.Core;

/// <summary>
/// Previews a <see cref="ModelAsset"/> by rendering it lit by a single directional light, framed to
/// the union of its submesh bounds.
/// </summary>
[AssetPreview(typeof(ModelAsset))]
public sealed class ModelAssetPreviewProvider : IScenePreviewProvider
{
    /// <inheritdoc/>
    public AssetPreviewScene BuildPreview(Asset asset, Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        if (asset is not ModelAsset modelAsset) return default;

        var root = new Node { Name = "__AssetPreview" };

        var modelNode = new Node { Name = "Model" };
        // Ownership transfers to modelNode; Dispose only clears cached asset lookups, nothing owned.
#pragma warning disable CA2000
        modelNode.AddComponent(new ModelComponent { Model = new AssetReference<ModelAsset>(modelAsset.Id) });
#pragma warning restore CA2000
        root.Children.Add(modelNode);

        root.Children.Add(PreviewLighting.CreateLightNode());

        var model = modelAsset.GetContent(vulkan);
        var bounds = Bounds.Empty;
        if (model is not null)
            foreach (var subMesh in model.SubMeshes)
                bounds = bounds.Encapsulate(subMesh.Bounds);

        if (bounds.IsEmpty) bounds = new Bounds(new(-0.5f), new(0.5f));

        return new AssetPreviewScene(root, bounds);
    }
}
