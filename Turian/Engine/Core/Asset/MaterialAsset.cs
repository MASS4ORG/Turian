namespace Turian.Engine.Core;

/// <summary>
/// PBR Metallic-Roughness material (glTF 2.0 default model). Texture references
/// are optional; when null the corresponding factor is used directly.
/// <see cref="GetContent"/> builds the descriptor set the renderer binds per submesh.
/// </summary>
[TypeId("a3000000-0000-4000-8000-00000000000a")]
public class MaterialAsset : Asset
{
    // Keyed by (assetId, context) rather than by device: a MaterialResource holds a descriptor set
    // allocated from one specific context's pool, and its fallback textures belong to that context
    // too. Keying by device would hand a resource built for the Scene View's context to Play mode's,
    // and disposing either would leave the other binding a freed descriptor set.
    // The cache owns the MaterialResource lifetime — callers must not dispose the returned instance.
    static readonly ConcurrentDictionary<(Guid, MaterialDescriptorContext), MaterialResource> resourceCache = new();

    /// <summary>RGBA base color factor. Multiplied with the base color texture sample if present.</summary>
    public Vector4 BaseColorFactor { get; set; } = new(1f, 1f, 1f, 1f);

    /// <summary>Metallic factor in [0,1]. Multiplied with the B channel of the metallic-roughness texture.</summary>
    public float MetallicFactor { get; set; } = 1.0f;

    /// <summary>Roughness factor in [0,1]. Multiplied with the G channel of the metallic-roughness texture.</summary>
    public float RoughnessFactor { get; set; } = 1.0f;

    /// <summary>Emissive RGB factor. Multiplied with the emissive texture sample if present.</summary>
    public Vector3 EmissiveFactor { get; set; } = new(0f, 0f, 0f);

    /// <summary>Optional sRGB base color texture.</summary>
    public AssetReference<TextureAsset>? BaseColorTexture { get; set; }

    /// <summary>Optional metallic-roughness texture (B = metallic, G = roughness, linear).</summary>
    public AssetReference<TextureAsset>? MetallicRoughnessTexture { get; set; }

    /// <summary>Optional tangent-space normal map.</summary>
    public AssetReference<TextureAsset>? NormalTexture { get; set; }

    /// <summary>Optional ambient-occlusion texture (R channel, linear).</summary>
    public AssetReference<TextureAsset>? OcclusionTexture { get; set; }

    /// <summary>Optional sRGB emissive texture.</summary>
    public AssetReference<TextureAsset>? EmissiveTexture { get; set; }

    /// <summary>
    /// Removes the cached <see cref="MaterialResource"/> for <paramref name="assetId"/> and
    /// disposes it. Call this when an asset is reimported so the next access rebuilds it.
    /// </summary>
    /// <param name="assetId">Identifier of the material asset.</param>
    public static void InvalidateCacheEntry(Guid assetId)
    {
        foreach (var key in resourceCache.Keys.Where(k => k.Item1 == assetId).ToList())
        {
            if (resourceCache.TryRemove(key, out var resource))
                resource.Dispose();
        }
    }

    /// <summary>
    /// Disposes all cached material resources and clears the cache.
    /// Call this on Vulkan device teardown.
    /// </summary>
    public static void ClearCache()
    {
        foreach (var (_, resource) in resourceCache)
            resource.Dispose();
        resourceCache.Clear();
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

        foreach (var key in resourceCache.Keys.Where(k => k.Item2 == context).ToList())
        {
            if (resourceCache.TryRemove(key, out var resource))
                resource.Dispose();
        }
    }

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

        var cacheKey = (Id, context);
        if (resourceCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // An AssetReference resolves to identity only; the factors and texture references
        // come from the artifact the importer wrote. Without one there is no material here,
        // and the caller binds its own default rather than this instance's unset values.
        var hydrated = Load(Id);
        if (hydrated is null)
        {
            return null;
        }

        var resource = new MaterialResource(context, hydrated);
        resourceCache[cacheKey] = resource;
        return resource;
    }

    /// <summary>
    /// Reads a material's stored property values from its artifact.
    /// </summary>
    /// <param name="assetId">Identifier of the material asset.</param>
    /// <returns>The material, or <c>null</c> when it is unknown or its artifact cannot be read.</returns>
    public static MaterialAsset? Load(Guid assetId)
    {
        if (assetId == Guid.Empty
            || !AssetDatabase.Instance.TryGetAssetProvider(assetId, out var provider)
            || provider is null)
        {
            return null;
        }

        try
        {
            using var stream = provider.GetAssetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var loaded = Serializer.LoadData<MaterialAsset>(reader.ReadToEnd());
            if (loaded is not null)
                loaded.Id = assetId;

            return loaded;
        }
        catch (Exception ex)
        {
            Log.Logger.LogWarning(ex, "Failed to read material asset {AssetId}", assetId);
            return null;
        }
    }
}
