namespace Turian.Engine.Core;

/// <summary>Builds and caches the descriptor set the renderer binds per submesh for a <see cref="MaterialAsset"/>.</summary>
public static class MaterialAssetContent
{
    // Keyed by (assetId, context) rather than by device: a MaterialResource holds a descriptor set
    // allocated from one specific context's pool, and its fallback textures belong to that context
    // too. Keying by device would hand a resource built for the Scene View's context to Play mode's,
    // and disposing either would leave the other binding a freed descriptor set.
    // The cache owns the MaterialResource lifetime — callers must not dispose the returned instance.
    static readonly ConcurrentDictionary<(Guid, MaterialDescriptorContext), MaterialResource> ResourceCache = new();

    extension(MaterialAsset asset)
    {
        /// <summary>
        /// Builds, or returns from cache, the descriptor set bound at set 1 for this material.
        /// The returned resource is owned by the cache — do not dispose it.
        /// </summary>
        /// <param name="context">The layout, pool and fallback textures shared by all materials.</param>
        /// <returns>
        /// The material's GPU resource, or <c>null</c> when no material is stored under this id.
        /// </returns>
        public MaterialResource? GetContent(MaterialDescriptorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var cacheKey = (asset.Id, context);
            if (ResourceCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            // An AssetReference resolves to identity only; the factors and texture references
            // come from the artifact the importer wrote. Without one there is no material here,
            // and the caller binds its own default rather than this instance's unset values.
            var hydrated = MaterialAsset.Load(context.Assets, asset.Id);
            if (hydrated is null)
            {
                return null;
            }

            var resource = new MaterialResource(context, hydrated);
            ResourceCache[cacheKey] = resource;
            return resource;
        }
    }

    extension(MaterialAsset)
    {
        /// <summary>
        /// Removes the cached <see cref="MaterialResource"/> for <paramref name="assetId"/> and
        /// disposes it. Call this when an asset is reimported so the next access rebuilds it.
        /// </summary>
        /// <param name="assetId">Identifier of the material asset.</param>
        public static void InvalidateCacheEntry(Guid assetId)
        {
            foreach (var key in ResourceCache.Keys.Where(k => k.Item1 == assetId).ToList())
            {
                if (ResourceCache.TryRemove(key, out var resource))
                {
                    resource.Dispose();
                }
            }
        }

        /// <summary>
        /// Disposes all cached material resources and clears the cache.
        /// Call this on Vulkan device teardown.
        /// </summary>
        public static void ClearCache()
        {
            foreach (var (_, resource) in ResourceCache)
            {
                resource.Dispose();
            }

            ResourceCache.Clear();
        }

        /// <summary>
        /// Disposes and forgets every resource built against <paramref name="context"/>. Called when a
        /// context is disposed, because its descriptor pool dies with it and any cached set allocated
        /// from that pool would be dangling.
        /// </summary>
        /// <param name="context">The context being torn down.</param>
        public static void EvictResourcesFor(MaterialDescriptorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            foreach (var key in ResourceCache.Keys.Where(k => k.Item2 == context).ToList())
            {
                if (ResourceCache.TryRemove(key, out var resource))
                {
                    resource.Dispose();
                }
            }
        }
    }
}
