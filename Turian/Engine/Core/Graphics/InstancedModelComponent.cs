namespace Turian.Engine.Core;

/// <summary>One instance of an <see cref="InstancedModelComponent"/>: its placement and per-instance shader data.</summary>
/// <param name="Transform">The placement relative to the owning node.</param>
/// <param name="Color">A linear tint multiplied into the material's base color.</param>
/// <param name="CustomData">Four free values passed to the shader unchanged.</param>
public readonly record struct MeshInstance(Transform Transform, Color Color, Vector4 CustomData)
{
    /// <summary>Creates an untinted instance with zero custom data.</summary>
    /// <param name="transform">The placement relative to the owning node.</param>
    public MeshInstance(Transform transform) : this(transform, Color.White, Vector4.Zero)
    {
    }
}

/// <summary>
/// Draws one model many times in a single instanced draw per submesh, like Unity's
/// <c>Graphics.RenderMeshInstanced</c>. Instances are placed relative to the owning node; edits through
/// <see cref="SetInstance"/>, <see cref="AddInstance"/> and <see cref="RemoveAt"/> upload only the changed range.
/// </summary>
[ComponentContextMenu("Rendering/Instanced Model")]
[TypeId("ee15ee55-5bc2-4b74-ac2f-fccf5858249a")]
[PublicAPI]
public class InstancedModelComponent : Component, IDisposable
{
    List<MeshInstance> instances = [];
    int dirtyStart = int.MaxValue;
    int dirtyEnd;
    int dirtySince;
    Model? model;
    Guid loadedModelAssetId;
    bool loadFailed;
    Model? boundsModel;
    int boundsVersion = -1;
    Bounds localBounds = Bounds.Empty;

    /// <summary>Gets or sets the model drawn for every instance.</summary>
    [JsonPropertyName("Model")]
    public AssetReference<ModelAsset>? Model { get; set; }

    /// <summary>
    /// Gets or sets the material every submesh draws with; when empty, each submesh uses the material the importer
    /// derived for it.
    /// </summary>
    [JsonPropertyName("Material")]
    public AssetReference<MaterialAsset>? Material { get; set; }

    /// <summary>Gets or sets every instance; assigning the list marks all of them changed.</summary>
    [JsonPropertyName("Instances")]
    public IReadOnlyList<MeshInstance> Instances
    {
        get => instances;
        set
        {
            instances = value is null ? [] : [.. value];
            MarkChanged(0, instances.Count);
        }
    }

    /// <summary>Gets the number of instances.</summary>
    [JsonIgnore]
    public int Count => instances.Count;

    /// <summary>Gets the instance at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based instance index.</param>
    public MeshInstance this[int index] => instances[index];

    /// <summary>Gets a number that changes whenever any instance changes.</summary>
    [JsonIgnore, Hide]
    public int Version { get; private set; }

    /// <summary>The render device bound to this scene, or <c>null</c> in a non-rendering host.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public IRenderDevice? RenderDevice { get; private set; }

    /// <summary>The asset database the model is read from, or <c>null</c> outside a project.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public AssetDatabase? Assets { get; private set; }

    /// <summary>Gets the loaded runtime model, or <c>null</c> until it resolves; a failed load is not retried.</summary>
    [JsonIgnore, Hide]
    public Model? ModelInstance
    {
        get
        {
            var modelAssetId = ModelAssetId;
            if (modelAssetId != loadedModelAssetId)
            {
                model = null;
                loadFailed = false;
                loadedModelAssetId = modelAssetId;
            }

            if (model is not null || loadFailed || modelAssetId == Guid.Empty) return model;
            if (RenderDevice is not { } device || Assets is not { } assets) return null;

            model = device.GetModel(modelAssetId, assets);
            loadFailed = model is null;
            return model;
        }
    }

    /// <summary>Gets the id of the model asset drawn, or <see cref="System.Guid.Empty"/> when none is set.</summary>
    [JsonIgnore, Hide]
    public Guid ModelAssetId => Model is { IsEmpty: false } reference ? reference.AssetId : Guid.Empty;

    /// <summary>Gets the bounds enclosing every instance in the node's space, or empty until the model loads.</summary>
    [JsonIgnore, Hide]
    public Bounds LocalBounds
    {
        get
        {
            var source = ModelInstance;
            if (boundsVersion == Version && ReferenceEquals(boundsModel, source)) return localBounds;
            boundsModel = source;
            boundsVersion = Version;
            localBounds = Bounds.Empty;
            if (source is null) return localBounds;
            foreach (var instance in instances)
                localBounds = localBounds.Encapsulate(
                    ModelBoundsUtility.ToWorldBounds(source.Bounds, instance.Transform.Matrix4X4()));
            return localBounds;
        }
    }

    /// <summary>Gets the world bounds enclosing every instance, used to cull the whole batch at once.</summary>
    [JsonIgnore, Hide]
    public Bounds WorldBounds => Node is { } node
        ? ModelBoundsUtility.ToWorldBounds(LocalBounds, node.GlobalTransform.Matrix4X4())
        : LocalBounds;

    /// <summary>Gets the world bounds of one instance, or empty until the model loads.</summary>
    /// <param name="index">The zero-based instance index.</param>
    public Bounds GetInstanceWorldBounds(int index)
    {
        if (ModelInstance is not { } source) return Bounds.Empty;
        var matrix = instances[index].Transform.Matrix4X4();
        if (Node is { } node) matrix *= node.GlobalTransform.Matrix4X4();
        return ModelBoundsUtility.ToWorldBounds(source.Bounds, matrix);
    }

    /// <summary>Replaces the instance at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based instance index.</param>
    /// <param name="instance">The new instance.</param>
    public void SetInstance(int index, MeshInstance instance)
    {
        instances[index] = instance;
        MarkChanged(index, index + 1);
    }

    /// <summary>Appends an instance.</summary>
    /// <param name="instance">The instance to add.</param>
    /// <returns>The new instance's index.</returns>
    public int AddInstance(MeshInstance instance)
    {
        instances.Add(instance);
        MarkChanged(instances.Count - 1, instances.Count);
        return instances.Count - 1;
    }

    /// <summary>Removes the instance at <paramref name="index"/>; later instances move down by one.</summary>
    /// <param name="index">The zero-based instance index.</param>
    public void RemoveAt(int index)
    {
        instances.RemoveAt(index);
        MarkChanged(index, instances.Count);
    }

    /// <summary>Removes every instance.</summary>
    public void Clear()
    {
        instances.Clear();
        MarkChanged(0, 0);
    }

    /// <summary>
    /// Gets the instances a consumer synchronized at <paramref name="syncedVersion"/> must refresh: the changed range,
    /// or every instance when changes since then were already acknowledged by another consumer.
    /// </summary>
    /// <param name="syncedVersion">The <see cref="Version"/> the consumer last uploaded, or -1 for none.</param>
    /// <param name="start">The first instance to refresh.</param>
    /// <param name="end">One past the last instance to refresh.</param>
    /// <returns><c>false</c> when nothing changed since <paramref name="syncedVersion"/>.</returns>
    internal bool TryGetChanges(int syncedVersion, out int start, out int end)
    {
        start = 0;
        end = instances.Count;
        if (syncedVersion == Version) return false;
        if (syncedVersion < dirtySince) return true;
        start = Math.Min(dirtyStart, end);
        end = Math.Min(dirtyEnd, end);
        return true;
    }

    /// <summary>Starts a new changed range after a consumer has uploaded the current one.</summary>
    internal void AcknowledgeChanges()
    {
        dirtySince = Version;
        dirtyStart = int.MaxValue;
        dirtyEnd = 0;
    }

    void MarkChanged(int start, int end)
    {
        dirtyStart = Math.Min(dirtyStart, start);
        dirtyEnd = Math.Max(dirtyEnd, end);
        Version++;
    }

    /// <summary>Releases this component's reference to the cached model.</summary>
    public void Dispose()
    {
        model = null;
        boundsModel = null;
        loadedModelAssetId = Guid.Empty;
        loadFailed = false;
        GC.SuppressFinalize(this);
    }
}
