namespace Turian.Engine.Core;

/// <summary>
/// Default <see cref="IAssetLoader"/> backed by the <see cref="AssetDatabase"/>.
/// Loads assets by deserializing their meta file (which contains the polymorphic
/// asset payload) and caches the resulting instance by identifier so that all
/// callers of the same asset share the same object.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton, typeof(IAssetLoader))]
public sealed class RuntimeAssetLoader : IAssetLoader
{
    readonly AssetDatabase assetDatabase;
    readonly Dictionary<Guid, Asset> cache = new();
    readonly Dictionary<Guid, Task<Asset?>> inflight = new();
    readonly object gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeAssetLoader"/> class.
    /// </summary>
    public RuntimeAssetLoader(AssetDatabase assetDatabase)
    {
        this.assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    /// <inheritdoc />
    public async Task<TAsset?> LoadAsync<TAsset>(Guid assetId) where TAsset : Asset
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        Task<Asset?> task;
        lock (gate)
        {
            if (cache.TryGetValue(assetId, out var cached))
            {
                return cached as TAsset;
            }

            if (!inflight.TryGetValue(assetId, out task!))
            {
                task = LoadCoreAsync(assetId);
                inflight[assetId] = task;
            }
        }

        var asset = await task.ConfigureAwait(false);
        return asset as TAsset;
    }

    /// <inheritdoc />
    public async Task<TData?> LoadContentAsync<TData>(Guid assetId) where TData : DataAsset
    {
        if (await LoadAsync<DataAssetAsset>(assetId).ConfigureAwait(false) is not { } metadata
            || !assetDatabase.TryGetAsset(assetId, out var record)
            || record is null)
        {
            return null;
        }

        var content = metadata.GetContent(record.ProjectRootPath);
        if (content is not null) await ObjectReferences.ResolveAsync(content, this).ConfigureAwait(false);
        return content as TData;
    }

    /// <inheritdoc />
    public Task PreloadAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);

        var loads = assetIds.Distinct().Select(PreloadOneAsync).ToList();
        return loads.Count == 0 ? Task.CompletedTask : Task.WhenAll(loads).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task PreloadLabelAsync(string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var ids = assetDatabase.GetAssetsSnapshot()
            .Where(record => record.Labels.Contains(label, StringComparer.Ordinal))
            .Select(static record => record.AssetId)
            .ToList();
        return PreloadAsync(ids, cancellationToken);
    }

    async Task PreloadOneAsync(Guid assetId)
    {
        if (await LoadAsync<Asset>(assetId).ConfigureAwait(false) is DataAssetAsset)
            await LoadContentAsync<DataAsset>(assetId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryGetLoaded<TAsset>(Guid assetId, out TAsset? asset) where TAsset : Asset
    {
        lock (gate)
        {
            if (cache.TryGetValue(assetId, out var cached) && cached is TAsset typed)
            {
                asset = typed;
                return true;
            }
        }

        asset = null;
        return false;
    }

    /// <inheritdoc />
    public void Release(Guid assetId)
    {
        lock (gate)
        {
            cache.Remove(assetId);
        }
    }

    async Task<Asset?> LoadCoreAsync(Guid assetId)
    {
        try
        {
            var asset = await Task.Run(() => LoadFromDisk(assetId)).ConfigureAwait(false);

            lock (gate)
            {
                if (asset is not null)
                {
                    cache[assetId] = asset;
                }
                inflight.Remove(assetId);
            }

            return asset;
        }
        catch (UnresolvableTypeIdException ex)
        {
            if (assetDatabase.TryGetAsset(assetId, out var record) && record is not null)
            {
                ex.SourcePath = record.SourceRelativePath;
            }
            lock (gate)
            {
                inflight.Remove(assetId);
            }
            throw;
        }
        catch
        {
            lock (gate)
            {
                inflight.Remove(assetId);
            }
            throw;
        }
    }

    Asset? LoadFromDisk(Guid assetId)
    {
        if (!assetDatabase.TryGetAsset(assetId, out var record) || record is null)
        {
            return null;
        }

        var projectRoot = record.ProjectRootPath;
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(record.MetaRelativePath))
        {
            return CreateFromRecord(record);
        }

        var metaPath = Path.IsPathRooted(record.MetaRelativePath)
            ? record.MetaRelativePath
            : Path.GetFullPath(Path.Combine(projectRoot, record.MetaRelativePath));

        if (!File.Exists(metaPath))
        {
            return CreateFromRecord(record);
        }

        var asset = Asset.Load(metaPath);
        if (asset is not null)
        {
            asset.RelativePath = record.SourceRelativePath;
        }
        return asset;
    }

    /// <summary>
    /// Instantiates an asset from its catalog record alone. A built or play-mode game ships the
    /// imported payloads without the sources' meta files, and content is read by id from there.
    /// </summary>
    static Asset? CreateFromRecord(AssetRecord record)
    {
        if (!TypeRegistry.TryGetType(record.AssetTypeName, out var type)
            || type is null
            || !typeof(Asset).IsAssignableFrom(type)
            || Activator.CreateInstance(type) is not Asset asset)
        {
            return null;
        }

        asset.Id = record.AssetId;
        asset.RelativePath = record.SourceRelativePath;
        return asset;
    }
}
