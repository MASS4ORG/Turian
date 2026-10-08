namespace Turian.Editor.Core;

/// <summary>
/// Imports FBX model assets through Assimp into glTF 2.0 geometry and
/// emits one <see cref="MeshAsset"/> per mesh-bearing node, one <see cref="MaterialAsset"/> per
/// material, and a <see cref="Prefab"/> mirroring the node hierarchy. Textures referenced by the
/// file are registered as assets in place and bound by their own ids.
/// </summary>
public sealed partial class FbxModelImporter : IAssetImporter
{
    readonly object syncRoot = new();
    string cacheKey = string.Empty;
    FbxImport? cached;

    /// <inheritdoc/>
    public int Version => 3;

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && Path.GetExtension(filePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public object? ImportSettingsFor(Asset asset) => (asset as ModelImportAsset)?.ImportSettings;

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath) => new ModelImportAsset
    {
        RelativePath = filePath,
        ImportSettings = new ModelImportSettings { Format = "fbx" },
    };

    /// <inheritdoc/>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        var import = Read(sourcePath);
        var blobFileName = $"{IAssetImporter.PrimaryArtifactName}.glb";
        AssimpModelConverter.ConvertToGlb(sourcePath, Path.Combine(importDirectory, blobFileName));

        Log.Logger.LogInformation(
            "Imported FBX {Path}: {MeshCount} meshes, {SubMeshCount} submeshes, {MaterialCount} materials",
            sourcePath,
            import.Meshes.Count,
            import.SubMeshCount,
            import.Materials.Count);

        return [blobFileName];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Textures are files of their own, so they are registered as assets in place and referenced by
    /// their own ids; only materials, meshes and the prefab are children of the model.
    /// </remarks>
    public IEnumerable<Asset> CreateChildAssets(Guid parentAssetId, string filePath, IAssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var import = Read(filePath);
        var textureIds = ResolveTextures(import, filePath, context);

        for (var i = 0; i < import.Materials.Count; i++)
        {
            yield return BuildMaterial(parentAssetId, filePath, import.Materials[i], i, textureIds);
        }

        for (var i = 0; i < import.Meshes.Count; i++)
        {
            var mesh = import.Meshes[i];
            yield return new MeshAsset
            {
                Id = AssetIdFactory.Derive(parentAssetId, $"mesh:{i}"),
                RelativePath = $"{Path.GetFileName(filePath)}#mesh:{i}",
                Model = new AssetReference<ModelAsset>(parentAssetId),
                SubMeshStart = mesh.SubMeshStart,
                SubMeshCount = mesh.SubMeshCount,
                Bounds = mesh.Bounds,
            };
        }

        yield return new Prefab
        {
            Id = AssetIdFactory.Derive(parentAssetId, "prefab"),
            RelativePath = $"{Path.GetFileName(filePath)}#prefab",
        };
    }

    /// <inheritdoc/>
    public string? CreateChildAssetContent(Guid parentAssetId, Asset child, string filePath)
    {
        if (child is not Prefab)
        {
            return null;
        }

        var import = Read(filePath);
        return Serializer.Serialize(BuildPrefabRoot(parentAssetId, import, Path.GetFileNameWithoutExtension(filePath)));
    }

    /// <summary>
    /// Builds the node hierarchy the importer stores as the model's prefab child.
    /// </summary>
    /// <param name="parentAssetId">Id of the model asset the meshes belong to.</param>
    /// <param name="filePath">Absolute path of the FBX file.</param>
    public Node BuildPrefabRoot(Guid parentAssetId, string filePath) =>
        BuildPrefabRoot(parentAssetId, Read(filePath), Path.GetFileNameWithoutExtension(filePath));

    static Node BuildPrefabRoot(Guid parentAssetId, FbxImport import, string rootName)
    {
        var counter = 0;
        var root = BuildPrefabNode(parentAssetId, import.Root, ref counter);
        root.Name = rootName;
        return root;
    }

    static Node BuildPrefabNode(Guid parentAssetId, FbxNodeInfo source, ref int counter)
    {
        var node = new Node
        {
            Id = AssetIdFactory.Derive(parentAssetId, $"node:{counter++}"),
            Name = source.Name,
            Transform = new Transform
            {
                Position = source.Position,
                Orientation = source.Orientation,
                Scale = source.Scale,
            },
        };

        if (source.MeshIndex >= 0)
        {
            var component = node.AddComponent<ModelComponent>();
            component.Mesh = new AssetReference<MeshAsset>(AssetIdFactory.Derive(parentAssetId, $"mesh:{source.MeshIndex}"));
        }

        foreach (var child in source.Children)
        {
            var childNode = BuildPrefabNode(parentAssetId, child, ref counter);
            childNode.Parent = node;
            node.Children.Add(childNode);
        }

        return node;
    }

    /// <summary>
    /// Registers every texture the file names as an asset of its own and records the color space
    /// its slot implies, returning one asset id per texture path.
    /// </summary>
    /// <param name="import">The parsed file.</param>
    /// <param name="filePath">Absolute path of the FBX file, which texture paths are relative to.</param>
    /// <param name="context">The import pipeline.</param>
    /// <returns>Asset ids indexed by texture, <see cref="System.Guid.Empty"/> where the file is missing.</returns>
    static Guid[] ResolveTextures(FbxImport import, string filePath, IAssetImportContext context)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var ids = new Guid[import.TexturePaths.Count];

        for (var i = 0; i < import.TexturePaths.Count; i++)
        {
            var texturePath = Path.GetFullPath(Path.Combine(directory, import.TexturePaths[i]));
            ids[i] = context.EnsureAsset(texturePath);
            if (ids[i] == Guid.Empty)
            {
                continue;
            }

            context.ConfigureTexture(
                texturePath,
                isSrgb: import.SrgbTextures.Contains(i),
                flipGreenChannel: import.GreenFlippedTextures.Contains(i));
        }

        return ids;
    }

    static MaterialAsset BuildMaterial(
        Guid parentAssetId,
        string filePath,
        FbxMaterialInfo material,
        int index,
        Guid[] textureIds)
    {
        AssetReference<TextureAsset>? Reference(int textureIndex) =>
            textureIndex < 0 || textureIndex >= textureIds.Length || textureIds[textureIndex] == Guid.Empty
                ? null
                : new AssetReference<TextureAsset> { AssetId = textureIds[textureIndex] };

        return new MaterialAsset
        {
            Id = AssetIdFactory.Derive(parentAssetId, $"material:{index}"),
            RelativePath = $"{Path.GetFileName(filePath)}#material:{index}",
            BaseColorTexture = Reference(material.BaseColor),
            // ORCA packs occlusion, roughness and metalness into the legacy specular slot.
            MetallicRoughnessTexture = Reference(material.OcclusionRoughnessMetallic),
            OcclusionTexture = Reference(material.OcclusionRoughnessMetallic),
            NormalTexture = Reference(material.Normal),
            EmissiveTexture = Reference(material.Emissive),
            EmissiveFactor = material.Emissive >= 0 ? new Vector3(1f, 1f, 1f) : new Vector3(0f, 0f, 0f),
        };
    }
}
