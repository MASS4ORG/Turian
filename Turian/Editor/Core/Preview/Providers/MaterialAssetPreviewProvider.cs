namespace Turian.Editor.Core;

/// <summary>
/// Previews a <see cref="MaterialAsset"/> on <see cref="PreviewQuadMesh"/>, Unity's material-ball
/// preview simplified to a swatch since the engine has no built-in primitive sphere mesh.
/// </summary>
[AssetPreview(typeof(MaterialAsset))]
public sealed class MaterialAssetPreviewProvider : IScenePreviewProvider
{
    static readonly Bounds QuadBounds = new(new(-0.5f, -0.5f, -0.01f), new(0.5f, 0.5f, 0.01f));

    /// <inheritdoc/>
    public AssetPreviewScene BuildPreview(Asset asset, Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        if (asset is not MaterialAsset materialAsset) return default;

        var root = new Node { Name = "__AssetPreview" };

        var quadNode = new Node { Name = "Swatch" };
        // Ownership transfers to quadNode; Dispose only clears cached asset lookups, nothing owned.
#pragma warning disable CA2000
        quadNode.AddComponent(new ModelComponent
        {
            ModelOverride = PreviewQuadMesh.Get(vulkan),
            Materials = [new AssetReference<MaterialAsset>(materialAsset.Id)],
        });
#pragma warning restore CA2000
        root.Children.Add(quadNode);

        root.Children.Add(PreviewLighting.CreateLightNode());

        return new AssetPreviewScene(root, QuadBounds);
    }
}
