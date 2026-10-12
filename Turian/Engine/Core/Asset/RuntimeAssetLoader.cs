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
    Dictionary<string, OapMountSet?>? sessionMounts;

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
                // The worker's cleanup takes gate after its task has been registered.
                task = Task.Run(() => LoadCore(assetId));
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

        // Packaged content has no sources to prefer over the imported copy.
        var projectPath = assetDatabase.Content is null ? record.ProjectRootPath : string.Empty;
        var content = metadata.GetContent(projectPath, ReadBaseFromSession, GetProvider(assetId));
        if (content is not null) await ObjectReferences.ResolveAsync(content, this).ConfigureAwait(false);
        return content as TData;
    }

    IAssetFileProvider? GetProvider(Guid assetId)
    {
        if (!assetDatabase.TryGetAsset(assetId, out var record) || record is null)
            return null;
        if (record.StorageKind != AssetStorageKind.Oap)
            return assetDatabase.TryGetAssetProvider(assetId, out var provider) ? provider : null;

        var content = assetDatabase.Content;
        var path = PackagePath(record, content);
        var mount = GetSessionMount(path, content);
        return content is null
            ? new OapAssetFileProvider(path, record.PrimaryContentKey, mount)
            : new OapAssetFileProvider(content, path, record.PrimaryContentKey, mount);
    }

    // A runtime catalog names packages inside its content root; a project names package files on disk.
    static string PackagePath(AssetRecord record, IContentDirectory? content) =>
        content is null ? record.ResolveContentPath() : AssetDatabase.ContentPath(record.ImportedRelativePath);

    OapMountSet GetSessionMount(string path, IContentDirectory? content)
    {
        lock (gate)
        {
            sessionMounts ??= CaptureMounts(content);

            if (!sessionMounts.TryGetValue(path, out var mount) || mount is null)
                throw new FileNotFoundException("OAP content is absent from this session.", path);
            return mount;
        }
    }

    Dictionary<string, OapMountSet?> CaptureMounts(IContentDirectory? content) =>
        assetDatabase.GetAssetsSnapshot()
            .Where(static asset => asset.StorageKind == AssetStorageKind.Oap)
            .Select(asset => PackagePath(asset, content))
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static candidate => candidate,
                candidate => content is null
                    ? OapMountSet.ForBasePackage(candidate)
                    : OapMountSet.ForBasePackage(content, candidate),
                StringComparer.OrdinalIgnoreCase);

    string? ReadBaseFromSession(Guid assetId)
    {
        if (GetProvider(assetId) is not { } provider) return null;
        using var reader = new StreamReader(provider.GetAssetStream());
        return reader.ReadToEnd();
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

    Asset? LoadCore(Guid assetId)
    {
        try
        {
            var asset = LoadFromDisk(assetId);

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

        var asset = assetDatabase.Content is { } content ? LoadMeta(content, record) : LoadMeta(record);
        if (asset is null)
        {
            return CreateFromRecord(record);
        }

        asset.RelativePath = record.SourceRelativePath;
        return asset;
    }

    static Asset? LoadMeta(AssetRecord record) => MetaPath(record) is { } metaPath ? Asset.Load(metaPath) : null;

    // A built game normally ships without meta files; one present in its content still describes the asset.
    static Asset? LoadMeta(IContentDirectory content, AssetRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.MetaRelativePath) || Path.IsPathRooted(record.MetaRelativePath)
            || record.MetaRelativePath.StartsWith("..", StringComparison.Ordinal))
            return null;

        var metaPath = AssetDatabase.ContentPath(record.MetaRelativePath);
        return content.Exists(metaPath) ? Serializer.LoadData<Asset>(content.ReadAllText(metaPath)) : null;
    }

    // The record's existing meta file, or null when the game ships without it.
    static string? MetaPath(AssetRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ProjectRootPath) || string.IsNullOrWhiteSpace(record.MetaRelativePath))
        {
            return null;
        }

        var metaPath = Path.IsPathRooted(record.MetaRelativePath)
            ? record.MetaRelativePath
            : Path.GetFullPath(Path.Combine(record.ProjectRootPath, record.MetaRelativePath));
        return File.Exists(metaPath) ? metaPath : null;
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
