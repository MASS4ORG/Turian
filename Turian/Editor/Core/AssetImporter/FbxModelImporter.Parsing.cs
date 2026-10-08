namespace Turian.Editor.Core;

/// <summary>
/// Imports FBX model assets through Assimp. Exports geometry as glTF 2.0 and
/// emits one <see cref="MeshAsset"/> per mesh-bearing node, one <see cref="MaterialAsset"/> per
/// material, and a <see cref="Prefab"/> mirroring the node hierarchy. Textures referenced by the
/// file are registered as assets in place and bound by their own ids.
/// </summary>
public sealed partial class FbxModelImporter
{
    FbxImport Read(string filePath)
    {
        var key = BuildCacheKey(filePath);
        lock (syncRoot)
        {
            if (cached is not null && string.Equals(cacheKey, key, StringComparison.Ordinal)) return cached;
            cached = Parse(filePath);
            cacheKey = key;
            return cached;
        }
    }

    static string BuildCacheKey(string filePath)
    {
        var info = new FileInfo(filePath);
        return $"{Path.GetFullPath(filePath)}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
    }

    static unsafe FbxImport Parse(string filePath)
    {
        var api = AssimpModelConverter.Api;
        var scene = api.ImportFile(filePath, AssimpModelConverter.PostProcessFlags);
        if (scene is null)
        {
            throw new InvalidDataException($"Assimp failed to read '{filePath}': {api.GetErrorStringS()}");
        }

        try
        {
            return Build(api, scene);
        }
        finally
        {
            api.ReleaseImport(scene);
        }
    }

    static unsafe FbxImport Build(AssimpApi api, AssimpScene* scene)
    {
        var textures = new TextureTable(api);
        var materials = new List<FbxMaterialInfo>((int)scene->MNumMaterials);
        for (uint i = 0; i < scene->MNumMaterials; i++)
        {
            var material = scene->MMaterials[i];
            materials.Add(new FbxMaterialInfo(
                textures.Add(material, AssimpTextureType.Diffuse, true, false),
                textures.Add(material, AssimpTextureType.Specular, false, false),
                textures.Add(material, AssimpTextureType.Normals, false, true),
                textures.Add(material, AssimpTextureType.Emissive, true, false)));
        }

        var meshes = new List<FbxMeshInfo>();
        uint subMeshCount = 0;
        var root = BuildNode(scene, scene->MRootNode, meshes, ref subMeshCount);
        return new FbxImport(meshes, subMeshCount, materials, textures.Paths, textures.Srgb, textures.GreenFlipped, root);
    }

    sealed class TextureTable(AssimpApi api)
    {
        internal readonly List<string> Paths = [];
        internal readonly HashSet<int> Srgb = [];
        internal readonly HashSet<int> GreenFlipped = [];
        readonly Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);

        internal unsafe int Add(AssimpMaterial* material, AssimpTextureType type, bool isSrgb, bool flipGreen)
        {
            if (api.GetMaterialTextureCount(material, type) == 0) return -1;
            AssimpString path;
            _ = api.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null);
            var normalized = path.AsString.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized)) return -1;
            if (!indices.TryGetValue(normalized, out var index))
            {
                index = Paths.Count;
                indices[normalized] = index;
                Paths.Add(normalized);
            }
            if (isSrgb) Srgb.Add(index);
            if (flipGreen) GreenFlipped.Add(index);
            return index;
        }
    }

    static unsafe FbxNodeInfo BuildNode(AssimpScene* scene, AssimpNode* node,
        List<FbxMeshInfo> meshes, ref uint subMeshCount)
    {
        var meshIndex = -1;
        var start = subMeshCount;
        var bounds = Bounds.Empty;
        for (uint i = 0; i < node->MNumMeshes; i++)
        {
            var mesh = scene->MMeshes[node->MMeshes[i]];
            if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0) continue;
            var min = mesh->MAABB.Min;
            var max = mesh->MAABB.Max;
            bounds = bounds.Encapsulate(new Bounds(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, max.Y, max.Z)));
            subMeshCount++;
        }
        if (subMeshCount > start)
        {
            meshIndex = meshes.Count;
            meshes.Add(new FbxMeshInfo(node->MName.AsString, start, subMeshCount - start, bounds));
        }
        var children = new List<FbxNodeInfo>((int)node->MNumChildren);
        for (uint i = 0; i < node->MNumChildren; i++)
            children.Add(BuildNode(scene, node->MChildren[i], meshes, ref subMeshCount));
        var (position, orientation, scale) = DecomposeTransform(node->MTransformation);
        return new FbxNodeInfo(node->MName.AsString, position, orientation, scale, meshIndex, children);
    }

    static (Vector3 Position, Quaternion Orientation, Vector3 Scale)
        DecomposeTransform(Matrix4x4 assimpTransform)
    {
        // Assimp stores column-vector matrices; System.Numerics composes row vectors.
        var local = Matrix4x4.Transpose(assimpTransform);
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
        {
            return (
                new Vector3(local.M41, local.M42, local.M43),
                Quaternion.Identity,
                Vector3.One);
        }

        return (
            new Vector3(translation.X, translation.Y, translation.Z),
            new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new Vector3(scale.X, scale.Y, scale.Z));
    }

    sealed record FbxImport(
        IReadOnlyList<FbxMeshInfo> Meshes,
        uint SubMeshCount,
        IReadOnlyList<FbxMaterialInfo> Materials,
        IReadOnlyList<string> TexturePaths,
        IReadOnlySet<int> SrgbTextures,
        IReadOnlySet<int> GreenFlippedTextures,
        FbxNodeInfo Root);

    sealed record FbxMeshInfo(string Name, uint SubMeshStart, uint SubMeshCount, Bounds Bounds);

    sealed record FbxMaterialInfo(
        int BaseColor,
        int OcclusionRoughnessMetallic,
        int Normal,
        int Emissive);

    sealed record FbxNodeInfo(
        string Name,
        Vector3 Position,
        Quaternion Orientation,
        Vector3 Scale,
        int MeshIndex,
        IReadOnlyList<FbxNodeInfo> Children);
}
