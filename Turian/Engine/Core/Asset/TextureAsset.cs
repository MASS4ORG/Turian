namespace Turian.Engine.Core;

/// <summary>
/// A 2D texture asset. <see cref="GetContent"/> reads the artifact the importer wrote and uploads
/// it to the GPU: an <c>.amtex</c> container goes up as-is, block compression and mip chain
/// intact, while any other payload is decoded to RGBA8 first. The resulting <see cref="Texture"/>
/// is cached per asset and device.
/// </summary>
[TypeId("a3000000-0000-4000-8000-00000000000b")]
public class TextureAsset : Asset
{
    // Keyed by (assetId, deviceHandle) so one texture never uploads twice per device.
    // The cache owns the Texture lifetime — callers must not dispose the returned instance.
    static readonly ConcurrentDictionary<(Guid, nint), Texture> textureCache = new();

    static long uploadedBytes;

    /// <summary>
    /// Bytes of device memory the currently cached textures occupy across every mip level and
    /// device. Grows as textures upload and shrinks when the cache is invalidated.
    /// </summary>
    public static ulong UploadedBytes => (ulong)Math.Max(0, Interlocked.Read(ref uploadedBytes));

    /// <summary>Number of textures currently held on the GPU.</summary>
    public static int UploadedCount => textureCache.Count;

    /// <summary>
    /// Whether the texture is sampled in sRGB color space. True for albedo /
    /// base-color / emissive textures; false for normal, metallic-roughness,
    /// and other linear data textures.
    /// </summary>
    public bool IsSrgb { get; set; } = true;

    /// <summary>
    /// Whether mipmaps should be generated when the texture is uploaded.
    /// </summary>
    public bool GenerateMips { get; set; } = true;

    /// <summary>
    /// Whether the green channel is inverted relative to the OpenGL/glTF normal-map convention.
    /// True for DirectX-convention normal maps. Applied by the shader, since block-compressed data
    /// cannot be rewritten at import without recompressing it.
    /// </summary>
    public bool FlipGreenChannel { get; set; }

    /// <summary>
    /// How the importer bakes this texture into cache artifacts.
    /// </summary>
    public TextureImportSettings ImportSettings { get; set; } = new();

    /// <summary>
    /// Removes the cached <see cref="Texture"/> for <paramref name="assetId"/> and disposes it.
    /// Call this when an asset is reimported so the next access reloads from disk.
    /// </summary>
    /// <param name="assetId">Identifier of the texture asset.</param>
    public static void InvalidateCacheEntry(Guid assetId)
    {
        foreach (var key in textureCache.Keys.Where(k => k.Item1 == assetId).ToList())
        {
            if (!textureCache.TryRemove(key, out var texture)) continue;

            Interlocked.Add(ref uploadedBytes, -(long)texture.SizeBytes);
            texture.Dispose();
        }
    }

    /// <summary>
    /// Disposes all cached textures and clears the cache. Call this on Vulkan device teardown.
    /// </summary>
    public static void ClearCache()
    {
        foreach (var (_, texture) in textureCache)
            texture.Dispose();
        textureCache.Clear();
        Interlocked.Exchange(ref uploadedBytes, 0);
        TextureMemoryReport.Reset();
    }

    /// <summary>
    /// Reads this texture's artifact and uploads it to the GPU.
    /// The returned <see cref="Texture"/> is owned by the cache — do not dispose it.
    /// </summary>
    /// <param name="vulkan">The Vulkan context the texture is created on.</param>
    /// <returns>The uploaded texture, or <c>null</c> when the artifact is missing or undecodable.</returns>
    public Texture? GetContent(Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);

        var cacheKey = (Id, vulkan.Device.VkDevice.Handle);
        if (textureCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
        {
            return null;
        }

        try
        {
            using var stream = provider.GetAssetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();

            Texture texture;
            if (TextureBlob.IsTextureBlob(bytes))
            {
                // The blob already carries the format, the resolution the import settings capped it
                // to, and the mip chain, so nothing is recomputed or decompressed here.
                var blob = TextureBlob.Read(bytes);
                texture = new Texture(vulkan, blob.Format, blob.Width, blob.Height, blob.Levels);
            }
            else
            {
                var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
                if (image is null || image.Data.Length == 0)
                {
                    Log.Logger.LogWarning("Texture asset {AssetId} ({RelativePath}) decoded to no data", Id, RelativePath);
                    return null;
                }

                texture = new Texture(vulkan, (uint)image.Width, (uint)image.Height, image.Data, IsSrgb, GenerateMips);
            }

            textureCache[cacheKey] = texture;
            Interlocked.Add(ref uploadedBytes, (long)texture.SizeBytes);
            return texture;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to load texture asset {AssetId} ({RelativePath})", Id, RelativePath);
            return null;
        }
    }
}
