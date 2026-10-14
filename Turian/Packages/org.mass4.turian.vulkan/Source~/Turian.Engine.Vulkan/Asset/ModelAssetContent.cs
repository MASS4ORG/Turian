namespace Turian.Engine.Core;

/// <summary>Uploads <see cref="ModelAsset"/> geometry to Vulkan and caches the result per asset and device.</summary>
public static class ModelAssetContent
{
    // Keyed by (assetId, Device.Id) so the same GUID is never loaded twice per Vulkan device.
    // Cache owns the Model lifetime — callers must not dispose the returned instance.
    static readonly ConcurrentDictionary<(Guid, long), VulkanModel> ModelCache = new();

    extension(ModelAsset asset)
    {
        /// <summary>
        /// Gets the content of this model asset by reading its glTF 2 geometry and registered external buffers.
        /// The returned <see cref="VulkanModel"/> is owned by the cache — do not dispose it.
        /// </summary>
        /// <param name="vulkan">The Vulkan context used for loading the model.</param>
        /// <param name="database">The asset database the model is registered in.</param>
        /// <returns>A loaded 3D model or <c>null</c> if the asset could not be found or loaded.</returns>
        public VulkanModel? GetContent(Vulkan vulkan, AssetDatabase database)
        {
            ArgumentNullException.ThrowIfNull(vulkan);
            ArgumentNullException.ThrowIfNull(database);
            var cacheKey = (asset.Id, vulkan.Device.Id);
            if (ModelCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            if (!database.TryGetAssetProvider(asset.Id, out var provider) || provider is null)
            {
                return null;
            }

            using var timing = database.LoadStatistics.Measure(texture: false);
            try
            {
                if (asset.ReadGeometry(database) is not { } geometry)
                {
                    return null;
                }

                var loaded = new VulkanModel(vulkan, geometry);
                ModelCache[cacheKey] = loaded;
                return loaded;
            }
            catch (Exception ex)
            {
                Log.Logger.LogError(ex, "Failed to load model asset {AssetId} ({Path})", asset.Id,
                    ModelAsset.ResolveProviderPath(provider));
                return null;
            }
        }
    }

    extension(ModelAsset)
    {
        /// <summary>
        /// Removes the cached <see cref="VulkanModel"/> for <paramref name="assetId"/> and disposes it.
        /// Call this when an asset is reimported so the next access reloads from disk.
        /// </summary>
        public static void InvalidateCacheEntry(Guid assetId)
        {
            foreach (var key in ModelCache.Keys.Where(k => k.Item1 == assetId).ToList())
            {
                if (ModelCache.TryRemove(key, out var model))
                {
                    model.Dispose();
                }
            }
        }

        /// <summary>
        /// Disposes all cached models and clears the cache. Call this on Vulkan device teardown.
        /// </summary>
        public static void ClearCache()
        {
            foreach (var (_, model) in ModelCache)
            {
                model.Dispose();
            }

            ModelCache.Clear();
        }
    }
}
