namespace Turian.Engine.Core;

/// <summary>
/// Reusable model rendering behavior that can be attached to any <see cref="Node"/>.
/// </summary>
[ComponentContextMenu("Rendering/Model")]
[TypeId("a3000001-0000-4000-8000-000000000008")]
[PublicAPI]
public class ModelComponent : Component, IDisposable
{
    /// <summary>
    /// Gets or sets the model asset reference. Every submesh of the model is drawn.
    /// </summary>
    [JsonPropertyName("Model")]
    public AssetReference<ModelAsset>? Model { get; set; }

    /// <summary>
    /// Gets or sets the mesh asset reference. When set it takes precedence over
    /// <see cref="Model"/>, and only the mesh's submesh range is drawn.
    /// </summary>
    [JsonPropertyName("Mesh")]
    public AssetReference<MeshAsset>? Mesh { get; set; }

    /// <summary>
    /// Gets or sets the per-submesh material overrides, Unity's
    /// <c>MeshRenderer.sharedMaterials[]</c> equivalent. Index order matches the model's
    /// submeshes; a null or missing entry uses the material the importer derived from the
    /// source file for that submesh.
    /// </summary>
    public List<AssetReference<MaterialAsset>?> Materials { get; set; } = [];

    Model? model;
    MeshAsset? meshAsset;
    Guid meshAssetId;
    Guid loadedModelAssetId;
    bool loadFailed;

    /// <summary>
    /// Gets or sets a model to draw instead of resolving <see cref="Model"/>/<see cref="Mesh"/>.
    /// Runtime-only — never serialized — for editor tooling that renders a procedural mesh with no
    /// backing asset, such as the material preview's display quad.
    /// </summary>
    [JsonIgnore, HideInEditor]
    public Model? ModelOverride { get; set; }

    /// <summary>
    /// Gets the loaded runtime model.
    /// </summary>
    /// <remarks>
    /// Caches the resolved model, keyed by the asset id it was built from, so pointing
    /// <see cref="Model"/> or <see cref="Mesh"/> somewhere else — the inspector assigning one, a
    /// script swapping one mid-session — takes effect on the next frame. Returns null while Vulkan is
    /// still initializing, and retries then. A load that reaches the asset and fails is attempted once
    /// per id: the component returns null from then on, until the reference changes again.
    /// </remarks>
    [JsonIgnore, HideInEditor]
    public Model? ModelInstance
    {
        get
        {
            if (ModelOverride is not null) return ModelOverride;

            var modelAssetId = ResolveModelAssetId();

            if (modelAssetId != loadedModelAssetId)
            {
                model = null;
                loadFailed = false;
                loadedModelAssetId = modelAssetId;
            }

            if (model is not null) return model;
            if (loadFailed || modelAssetId == Guid.Empty) return null;

            var vulkan = RuntimeServices.TryGet<Vulkan>();
            if (vulkan is null) return null;

            model = new ModelAsset { Id = modelAssetId }.GetContent(vulkan);
            loadFailed = model is null;
            return model;
        }
    }

    /// <summary>
    /// Gets the id of the model asset this component draws from, or <see cref="Guid.Empty"/>
    /// when neither reference resolves.
    /// </summary>
    [JsonIgnore, HideInEditor]
    public Guid ModelAssetId => ResolveModelAssetId();

    /// <summary>
    /// Gets the submesh range this component draws.
    /// </summary>
    /// <param name="start">Index of the first submesh to draw.</param>
    /// <param name="count">Number of consecutive submeshes to draw.</param>
    /// <returns><c>true</c> when a <see cref="Mesh"/> narrows the range; <c>false</c> to draw the whole model.</returns>
    public bool TryGetSubMeshRange(out int start, out int count)
    {
        _ = ResolveModelAssetId();

        if (meshAsset is null)
        {
            start = 0;
            count = 0;
            return false;
        }

        start = (int)meshAsset.SubMeshStart;
        count = (int)meshAsset.SubMeshCount;
        return true;
    }

    /// <summary>
    /// Releases this component's reference to the cached model.
    /// </summary>
    public void Dispose()
    {
        model = null;
        meshAsset = null;
        meshAssetId = Guid.Empty;
        loadedModelAssetId = Guid.Empty;
        loadFailed = false;
        GC.SuppressFinalize(this);
    }

    Guid ResolveModelAssetId()
    {
        if (Mesh is { IsEmpty: false })
        {
            if (meshAssetId != Mesh.AssetId)
            {
                meshAsset = MeshAsset.Resolve(Mesh.AssetId);
                meshAssetId = Mesh.AssetId;
            }

            return meshAsset?.Model?.AssetId ?? Guid.Empty;
        }

        meshAsset = null;
        meshAssetId = Guid.Empty;
        return Model is { IsEmpty: false } ? Model.AssetId : Guid.Empty;
    }
}
