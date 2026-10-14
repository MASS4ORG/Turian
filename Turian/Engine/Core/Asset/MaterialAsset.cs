namespace Turian.Engine.Core;

/// <summary>
/// PBR Metallic-Roughness material (glTF 2.0 default model). Texture references
/// are optional; when null the corresponding factor is used directly.
/// </summary>
[TypeId("0a3e5372-83ed-59ef-ae7d-124539527fac")]
public class MaterialAsset : Asset
{
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
    /// Reads a material's stored property values from its artifact.
    /// </summary>
    /// <param name="database">The asset database the material is registered in.</param>
    /// <param name="assetId">Identifier of the material asset.</param>
    /// <returns>The material, or <c>null</c> when it is unknown or its artifact cannot be read.</returns>
    public static MaterialAsset? Load(AssetDatabase database, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (assetId == Guid.Empty
            || !database.TryGetAssetProvider(assetId, out var provider)
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
            {
                loaded.Id = assetId;
            }

            return loaded;
        }
        catch (Exception ex)
        {
            Log.Logger.LogWarning(ex, "Failed to read material asset {AssetId}", assetId);
            return null;
        }
    }
}
