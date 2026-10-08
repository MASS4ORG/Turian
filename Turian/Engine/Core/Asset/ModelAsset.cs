namespace Turian.Engine.Core;

/// <summary>
/// Represents a model asset, which can be used to load a 3D model from resolved asset content.
/// </summary>
[TypeId("ddba9f04-28e9-58ba-8043-cde3e44bdbb1")]
public class ModelAsset : Asset
{
    // Keyed by (assetId, deviceHandle) so the same GUID is never loaded twice per Vulkan device.
    // Cache owns the Model lifetime — callers must not dispose the returned instance.
    static readonly ConcurrentDictionary<(Guid, nint), Model> ModelCache = new();

    /// <summary>
    /// Removes the cached <see cref="Model"/> for <paramref name="assetId"/> and disposes it.
    /// Call this when an asset is reimported so the next access reloads from disk.
    /// </summary>
    public static void InvalidateCacheEntry(Guid assetId)
    {
        foreach (var key in ModelCache.Keys.Where(k => k.Item1 == assetId).ToList())
        {
            if (ModelCache.TryRemove(key, out var model))
                model.Dispose();
        }
    }

    /// <summary>
    /// Disposes all cached models and clears the cache. Call this on Vulkan device teardown.
    /// </summary>
    public static void ClearCache()
    {
        foreach (var (_, model) in ModelCache)
            model.Dispose();
        ModelCache.Clear();
    }

    /// <summary>
    /// Gets the content of this model asset by reading its glTF 2 geometry and registered external buffers.
    /// The returned <see cref="Model"/> is owned by the cache — do not dispose it.
    /// </summary>
    /// <param name="vulkan">The Vulkan context used for loading the model.</param>
    /// <param name="database">The asset database the model is registered in.</param>
    /// <returns>A loaded 3D model or <c>null</c> if the asset could not be found or loaded.</returns>
    public Model? GetContent(Vulkan vulkan, AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        ArgumentNullException.ThrowIfNull(database);
        var cacheKey = (Id, vulkan.Device.VkDevice.Handle);
        if (ModelCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!database.TryGetAssetProvider(Id, out var provider) || provider is null)
        {
            return null;
        }

        try
        {
            using var assetStream = provider.GetAssetStream();
            var loaded = new Model(vulkan, GltfModelReader.Read(assetStream,
                (index, _) => ReadBuffer(database, AssetIdFactory.Derive(Id, $"buffer:{index}"))));
            ModelCache[cacheKey] = loaded;
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to load model asset {AssetId} ({Path})", Id, ResolveProviderPath(provider));
            return null;
        }
    }

    static byte[] ReadBuffer(AssetDatabase database, Guid id)
    {
        if (!database.TryGetAssetProvider(id, out var provider) || provider is null)
            throw new InvalidDataException($"Missing glTF buffer asset {id}.");
        using var stream = provider.GetAssetStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static string ResolveProviderPath(IAssetFileProvider provider) => provider switch
    {
        RealFileSystemProvider real => real.FilePath,
        LooseFileAssetProvider loose => loose.FilePath,
        _ => provider.GetType().Name,
    };
}
