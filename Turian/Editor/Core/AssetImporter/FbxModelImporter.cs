using Silk.NET.Assimp;

namespace Turian.Editor.Core;

/// <summary>
/// Imports FBX model assets through Assimp. Bakes the geometry into an <c>.ammesh</c> blob and
/// emits one <see cref="MeshAsset"/> per mesh-bearing node, one <see cref="MaterialAsset"/> per
/// material, and a <see cref="Prefab"/> mirroring the node hierarchy. Textures referenced by the
/// file are registered as assets in place and bound by their own ids.
/// </summary>
public sealed partial class FbxModelImporter : IAssetImporter
{
    // Ultz.Native.Assimp ships libassimp 5 and 6 side by side, under runtimes/<rid>/native.
    // Resolving those by file name alone relies on the host's RID probing, which does not find
    // them in every environment, so the full paths are tried first and version 6 before 5.
    static readonly string[] nativeLibraryNames = BuildNativeLibraryNames();

    static string[] BuildNativeLibraryNames()
    {
        string[] fileNames = OperatingSystem.IsWindows()
            ? ["Assimp64.dll", "Assimp32.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libassimp.6.dylib", "libassimp.5.dylib"]
                : ["libassimp.so.6", "libassimp.so.5"];

        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        List<string> directories =
        [
            AppContext.BaseDirectory,
            Path.Combine(runtimes, RuntimeInformation.RuntimeIdentifier, "native"),
            Path.Combine(runtimes, PortableRuntimeIdentifier(), "native")
        ];

        List<string> candidates =
        [
            .. from directory in directories.Distinct()
            from fileName in fileNames
            let path = Path.Combine(directory, fileName)
            where System.IO.File.Exists(path)
            select path,

            .. fileNames
        ];

        // Anything installed system-wide, by name, as a last resort.
        return [.. candidates];
    }

    static string PortableRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(),
        };

        return $"{platform}-{architecture}";
    }

    const uint postProcessFlags = (uint)(
        PostProcessSteps.Triangulate
        | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.GenerateBoundingBoxes);

    // Assimp reports FBX geometry Y-up; the engine's world space has +Y pointing down.
    static readonly Matrix4x4 yMirror = new(
        1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    static readonly Lazy<AssimpApi> assimp =
        new(() => new AssimpApi(AssimpApi.CreateDefaultContext(nativeLibraryNames)), isThreadSafe: true);

    readonly object syncRoot = new();
    string cacheKey = string.Empty;
    FbxImport? cached;

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
        var import = Read(sourcePath, keepGeometry: true);

        var blobFileName = $"{IAssetImporter.PrimaryArtifactName}{MeshBlob.FileExtension}";
        MeshBlobWriter.Save(Path.Combine(importDirectory, blobFileName), import.Content);

        Log.Logger.LogInformation(
            "Imported FBX {Path}: {MeshCount} meshes, {SubMeshCount} submeshes, {MaterialCount} materials",
            sourcePath,
            import.Content.Meshes.Count,
            import.Content.SubMeshes.Count,
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

        var import = Read(filePath, keepGeometry: false);
        var textureIds = ResolveTextures(import, filePath, context);

        for (var i = 0; i < import.Materials.Count; i++)
        {
            yield return BuildMaterial(parentAssetId, filePath, import.Materials[i], i, textureIds);
        }

        for (var i = 0; i < import.Content.Meshes.Count; i++)
        {
            var mesh = import.Content.Meshes[i];
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

        var import = Read(filePath, keepGeometry: false);
        return Serializer.Serialize(BuildPrefabRoot(parentAssetId, import, Path.GetFileNameWithoutExtension(filePath)));
    }

    /// <summary>
    /// Builds the node hierarchy the importer stores as the model's prefab child.
    /// </summary>
    /// <param name="parentAssetId">Id of the model asset the meshes belong to.</param>
    /// <param name="filePath">Absolute path of the FBX file.</param>
    public EngineNode BuildPrefabRoot(Guid parentAssetId, string filePath) =>
        BuildPrefabRoot(parentAssetId, Read(filePath, keepGeometry: false), Path.GetFileNameWithoutExtension(filePath));

    static EngineNode BuildPrefabRoot(Guid parentAssetId, FbxImport import, string rootName)
    {
        var counter = 0;
        var root = BuildPrefabNode(parentAssetId, import.Root, ref counter);
        root.Name = rootName;
        return root;
    }

    static EngineNode BuildPrefabNode(Guid parentAssetId, FbxNodeInfo source, ref int counter)
    {
        var node = new EngineNode
        {
            Id = AssetIdFactory.Derive(parentAssetId, $"node:{counter++}"),
            Name = source.Name,
        };

        node.Transform.Position = source.Position;
        node.Transform.Orientation = source.Orientation;
        node.Transform.Scale = source.Scale;

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
    /// <returns>Asset ids indexed by texture, <see cref="Guid.Empty"/> where the file is missing.</returns>
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
