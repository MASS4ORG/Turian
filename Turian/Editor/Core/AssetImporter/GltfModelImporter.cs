namespace Turian.Editor.Core;

/// <summary>
/// Import glTF 2.0 model assets (.gltf and .glb) with GLTF-specific import settings.
/// Emits one child <see cref="MaterialAsset"/> per glTF material and one child
/// <see cref="TextureAsset"/> per embedded image, with deterministic ids derived from the parent
/// via <see cref="AssetIdFactory.Derive"/>. Images stored as files of their own are registered
/// as assets in place and referenced by their own ids.
/// </summary>
public class GltfModelImporter : IAssetImporter
{
    static readonly string[] supportedExtensions = [".gltf", ".glb"];

    const string imageFragment = "#image:";

    /// <inheritdoc/>
    /// <remarks>
    /// Version 2 bakes the geometry into <c>.ammesh</c>; version 3 stores embedded images as
    /// child payloads and binds external ones to their own assets.
    /// </remarks>
    public int Version => 3;

    /// <inheritdoc/>
    public bool IsValid(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var extension = Path.GetExtension(filePath);
        return supportedExtensions.Contains(extension, StringComparer.InvariantCultureIgnoreCase);
    }

    /// <inheritdoc/>
    public object? ImportSettingsFor(Asset asset) => (asset as ModelImportAsset)?.ImportSettings;

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        var format = Path.GetExtension(filePath).ToUpperInvariant() switch
        {
            ".GLB" => "glb",
            _ => "gltf",
        };
        return new ModelImportAsset
        {
            RelativePath = filePath,
            ImportSettings = new ModelImportSettings { Format = format }
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bakes the geometry into an <c>.ammesh</c> blob and copies the buffers and images a
    /// <c>.gltf</c> file references into the cache.
    /// </remarks>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        var blobFileName = $"{IAssetImporter.PrimaryArtifactName}{MeshBlob.FileExtension}";
        var content = MeshBlobBaker.FromModelBuilder(
            ModelUtils.LoadGltfToBuilder(sourcePath),
            Path.GetFileNameWithoutExtension(sourcePath));
        MeshBlobWriter.Save(Path.Combine(importDirectory, blobFileName), content);

        List<string> artifacts = [blobFileName];
        if (Path.GetExtension(sourcePath).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            artifacts.AddRange(CopyDependencies(sourcePath, importDirectory));
        }

        return artifacts;
    }

    /// <inheritdoc/>
    public IEnumerable<Asset> CreateChildAssets(Guid parentAssetId, string filePath, IAssetImportContext context)
    {
        string json;
        try
        {
            json = Path.GetExtension(filePath).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                ? ModelUtils.LoadJsonFromGlb(filePath)
                : File.ReadAllText(filePath);
        }
        catch
        {
            yield break;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Map glTF texture index → image index (textures[].source)
        var textureToImage = BuildTextureToImageMap(root);
        var linearImages = CollectLinearImages(root, textureToImage);
        var (imageIds, embedded) = ResolveImages(parentAssetId, filePath, root, linearImages, context);

        for (var i = 0; i < imageIds.Length; i++)
        {
            if (!embedded[i]) continue;

            yield return new TextureAsset
            {
                Id = imageIds[i],
                RelativePath = $"{Path.GetFileName(filePath)}{imageFragment}{i}",
                IsSrgb = !linearImages.Contains(i),
            };
        }

        // Material assets — one per material
        if (root.TryGetProperty("materials", out var materials))
        {
            var i = 0;
            foreach (var mat in materials.EnumerateArray())
            {
                yield return BuildMaterial(parentAssetId, filePath, mat, i, textureToImage, imageIds);
                i++;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>An embedded image's payload is its encoded bytes, decoded like any image file.</remarks>
    public byte[]? CreateChildAssetBinaryContent(Guid parentAssetId, Asset child, string filePath)
    {
        if (child is not TextureAsset texture) return null;

        var fragment = texture.RelativePath.LastIndexOf(imageFragment, StringComparison.Ordinal);
        return fragment >= 0 && int.TryParse(texture.RelativePath.AsSpan(fragment + imageFragment.Length), out var index)
            ? ModelUtils.LoadGltfEmbeddedImage(filePath, index)
            : null;
    }

    /// <summary>
    /// Assigns every glTF image an asset id: an external file is registered as an asset of its own,
    /// an image embedded in a buffer or <c>data:</c> URI becomes a child of the model.
    /// </summary>
    /// <returns>
    /// Asset ids indexed by image, <see cref="Guid.Empty"/> where an external file is missing, and
    /// which of them are embedded.
    /// </returns>
    static (Guid[] Ids, bool[] Embedded) ResolveImages(
        Guid parentAssetId,
        string filePath,
        JsonElement root,
        HashSet<int> linearImages,
        IAssetImportContext context)
    {
        if (!root.TryGetProperty("images", out var images)) return ([], []);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var ids = new Guid[images.GetArrayLength()];
        var embedded = new bool[ids.Length];

        var i = 0;
        foreach (var image in images.EnumerateArray())
        {
            var uri = image.TryGetProperty("uri", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                ids[i] = AssetIdFactory.Derive(parentAssetId, $"image:{i}");
                embedded[i] = true;
            }
            else
            {
                var imagePath = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(uri)));
                ids[i] = context.EnsureAsset(imagePath);
                if (ids[i] != Guid.Empty)
                {
                    // glTF normal maps are OpenGL-convention (+Y), so the green channel is never flipped.
                    context.ConfigureTexture(imagePath, isSrgb: !linearImages.Contains(i), flipGreenChannel: false);
                }
            }

            i++;
        }

        return (ids, embedded);
    }

    /// <summary>
    /// Images sampled as data rather than color — normal, metallic-roughness and occlusion maps —
    /// which the glTF spec defines as linear.
    /// </summary>
    static HashSet<int> CollectLinearImages(JsonElement root, int[] textureToImage)
    {
        var linear = new HashSet<int>();
        if (!root.TryGetProperty("materials", out var materials)) return linear;

        foreach (var mat in materials.EnumerateArray())
        {
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr)
                && pbr.TryGetProperty("metallicRoughnessTexture", out var mr))
                Add(mr);
            if (mat.TryGetProperty("normalTexture", out var nt)) Add(nt);
            if (mat.TryGetProperty("occlusionTexture", out var ot)) Add(ot);
        }

        return linear;

        void Add(JsonElement texInfo)
        {
            if (ImageIndex(texInfo, textureToImage) is { } imageIndex) linear.Add(imageIndex);
        }
    }

    static int[] BuildTextureToImageMap(JsonElement root)
    {
        if (!root.TryGetProperty("textures", out var textures)) return [];
        var map = new int[textures.GetArrayLength()];
        var i = 0;
        foreach (var tex in textures.EnumerateArray())
            map[i++] = tex.TryGetProperty("source", out var s) ? s.GetInt32() : -1;
        return map;
    }

    static MaterialAsset BuildMaterial(
        Guid parentAssetId,
        string filePath,
        JsonElement mat,
        int index,
        int[] textureToImage,
        Guid[] imageIds)
    {
        var asset = new MaterialAsset
        {
            Id = AssetIdFactory.Derive(parentAssetId, $"material:{index}"),
            RelativePath = $"{Path.GetFileName(filePath)}#material:{index}",
        };

        if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            if (pbr.TryGetProperty("baseColorFactor", out var bc) && bc.GetArrayLength() == 4)
                asset.BaseColorFactor = new Vector4(
                    bc[0].GetSingle(), bc[1].GetSingle(), bc[2].GetSingle(), bc[3].GetSingle());

            if (pbr.TryGetProperty("metallicFactor", out var mf))
                asset.MetallicFactor = mf.GetSingle();

            if (pbr.TryGetProperty("roughnessFactor", out var rf))
                asset.RoughnessFactor = rf.GetSingle();

            asset.BaseColorTexture = TextureRef(pbr, "baseColorTexture", textureToImage, imageIds);
            asset.MetallicRoughnessTexture = TextureRef(pbr, "metallicRoughnessTexture", textureToImage, imageIds);
        }

        if (mat.TryGetProperty("emissiveFactor", out var ef) && ef.GetArrayLength() == 3)
            asset.EmissiveFactor = new Vector3(ef[0].GetSingle(), ef[1].GetSingle(), ef[2].GetSingle());

        if (mat.TryGetProperty("normalTexture", out var nt))
            asset.NormalTexture = TextureRefFromInfo(nt, textureToImage, imageIds);

        if (mat.TryGetProperty("occlusionTexture", out var ot))
            asset.OcclusionTexture = TextureRefFromInfo(ot, textureToImage, imageIds);

        if (mat.TryGetProperty("emissiveTexture", out var et))
            asset.EmissiveTexture = TextureRefFromInfo(et, textureToImage, imageIds);

        return asset;
    }

    static AssetReference<TextureAsset>? TextureRef(
        JsonElement parent,
        string property,
        int[] textureToImage,
        Guid[] imageIds) =>
        parent.TryGetProperty(property, out var texInfo) ? TextureRefFromInfo(texInfo, textureToImage, imageIds) : null;

    static AssetReference<TextureAsset>? TextureRefFromInfo(JsonElement texInfo, int[] textureToImage, Guid[] imageIds)
    {
        if (ImageIndex(texInfo, textureToImage) is not { } imageIndex || imageIndex >= imageIds.Length) return null;
        var id = imageIds[imageIndex];
        return id == Guid.Empty ? null : new AssetReference<TextureAsset> { AssetId = id };
    }

    static int? ImageIndex(JsonElement texInfo, int[] textureToImage)
    {
        if (!texInfo.TryGetProperty("index", out var idxElem)) return null;
        var textureIndex = idxElem.GetInt32();
        if (textureIndex < 0 || textureIndex >= textureToImage.Length) return null;
        var imageIndex = textureToImage[textureIndex];
        return imageIndex < 0 ? null : imageIndex;
    }

    static IEnumerable<string> CopyDependencies(string gltfSourcePath, string importDirectory)
    {
        var sourceDir = Path.GetDirectoryName(gltfSourcePath);
        if (string.IsNullOrEmpty(sourceDir)) yield break;

        HashSet<string> uris;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(gltfSourcePath));
            uris = CollectReferencedUris(doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.Logger.LogWarning(ex, "Failed to parse glTF for dependency discovery: {Path}", gltfSourcePath);
            yield break;
        }

        foreach (var uri in uris)
        {
            var src = Path.Combine(sourceDir, uri);
            if (!File.Exists(src))
            {
                Log.Logger.LogWarning("glTF referenced file not found, skipping: {Path}", src);
                continue;
            }

            var dest = Path.Combine(importDirectory, uri);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(src, dest, overwrite: true);
            yield return uri;
        }
    }

    static HashSet<string> CollectReferencedUris(JsonElement root)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in new[] { "buffers", "images" })
        {
            if (!root.TryGetProperty(prop, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("uri", out var uriElem)) continue;
                var uri = uriElem.GetString();
                if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                uris.Add(uri);
            }
        }
        return uris;
    }
}
