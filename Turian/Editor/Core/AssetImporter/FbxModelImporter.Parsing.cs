namespace Turian.Editor.Core;

/// <summary>
/// Imports FBX model assets through Assimp. Bakes the geometry into an <c>.ammesh</c> blob and
/// emits one <see cref="MeshAsset"/> per mesh-bearing node, one <see cref="MaterialAsset"/> per
/// material, and a <see cref="Prefab"/> mirroring the node hierarchy. Textures referenced by the
/// file are registered as assets in place and bound by their own ids.
/// </summary>
public sealed partial class FbxModelImporter
{
    FbxImport Read(string filePath, bool keepGeometry)
    {
        var key = BuildCacheKey(filePath);

        lock (syncRoot)
        {
            if (cached is not null && string.Equals(cacheKey, key, StringComparison.Ordinal) && !keepGeometry)
            {
                return cached;
            }

            var import = Parse(filePath);

            // The geometry is only needed while the blob is written; the tables the child assets
            // and the prefab need are small enough to keep.
            cacheKey = key;
            cached = import with
            {
                Content = import.Content with { Vertices = [], TexCoord1 = [], Indices = [] },
            };

            return keepGeometry ? import : cached;
        }
    }

    static string BuildCacheKey(string filePath)
    {
        var info = new FileInfo(filePath);
        return $"{Path.GetFullPath(filePath)}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
    }

    static unsafe FbxImport Parse(string filePath)
    {
        var api = Assimp.Value;
        var scene = api.ImportFile(filePath, postProcessFlags);
        if (scene is null)
        {
            throw new InvalidOperationException($"Assimp failed to read '{filePath}': {api.GetErrorStringS()}");
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
        var texturePaths = new List<string>();
        var textureIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var srgbTextures = new HashSet<int>();
        var greenFlippedTextures = new HashSet<int>();

        int Texture(AssimpMaterial* material, AssimpTextureType type, bool isSrgb, bool flipGreen)
        {
            if (api.GetMaterialTextureCount(material, type) == 0)
            {
                return -1;
            }

            AssimpString path;
            _ = api.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null);
            var normalized = path.AsString.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return -1;
            }

            if (!textureIndices.TryGetValue(normalized, out var index))
            {
                index = texturePaths.Count;
                textureIndices[normalized] = index;
                texturePaths.Add(normalized);
            }

            if (isSrgb) srgbTextures.Add(index);
            if (flipGreen) greenFlippedTextures.Add(index);
            return index;
        }

        var materials = new List<FbxMaterialInfo>((int)scene->MNumMaterials);
        for (uint i = 0; i < scene->MNumMaterials; i++)
        {
            var material = scene->MMaterials[i];

            materials.Add(new FbxMaterialInfo(
                Texture(material, AssimpTextureType.Diffuse, isSrgb: true, flipGreen: false),
                Texture(material, AssimpTextureType.Specular, isSrgb: false, flipGreen: false),
                Texture(material, AssimpTextureType.Normals, isSrgb: false, flipGreen: true),
                Texture(material, AssimpTextureType.Emissive, isSrgb: true, flipGreen: false)));
        }

        var vertices = new List<Vertex>();
        var texCoord1 = new List<Vector2>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();
        var meshes = new List<MeshBlobMesh>();

        var root = BuildNode(scene, scene->MRootNode, vertices, texCoord1, indices, subMeshes, meshes);

        var sceneBounds = meshes.Aggregate(Bounds.Empty, static (acc, mesh) => acc.Encapsulate(mesh.Bounds));

        return new FbxImport(
            new MeshBlobContent
            {
                Vertices = [.. vertices],
                TexCoord1 = texCoord1.Count == vertices.Count ? [.. texCoord1] : [],
                Indices = [.. indices],
                SubMeshes = subMeshes,
                Meshes = meshes,
                Bounds = sceneBounds.IsEmpty ? default : sceneBounds,
            },
            materials,
            texturePaths,
            srgbTextures,
            greenFlippedTextures,
            root);
    }

    static unsafe FbxNodeInfo BuildNode(
        AssimpScene* scene,
        AssimpNode* node,
        List<Vertex> vertices,
        List<Vector2> texCoord1,
        List<uint> indices,
        List<SubMesh> subMeshes,
        List<MeshBlobMesh> meshes)
    {
        var meshIndex = -1;

        if (node->MNumMeshes > 0)
        {
            var subMeshStart = subMeshes.Count;
            var meshBounds = Bounds.Empty;

            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                var mesh = scene->MMeshes[node->MMeshes[i]];
                var subMesh = AppendMesh(mesh, vertices, texCoord1, indices);
                if (subMesh is null)
                {
                    continue;
                }

                subMeshes.Add(subMesh);
                meshBounds = meshBounds.Encapsulate(subMesh.Bounds);
            }

            if (subMeshes.Count > subMeshStart)
            {
                meshIndex = meshes.Count;
                meshes.Add(new MeshBlobMesh(
                    node->MName.AsString,
                    (uint)subMeshStart,
                    (uint)(subMeshes.Count - subMeshStart),
                    meshBounds));
            }
        }

        var children = new List<FbxNodeInfo>((int)node->MNumChildren);
        for (uint i = 0; i < node->MNumChildren; i++)
        {
            children.Add(BuildNode(scene, node->MChildren[i], vertices, texCoord1, indices, subMeshes, meshes));
        }

        var (position, orientation, scale) = DecomposeTransform(node->MTransformation);
        return new FbxNodeInfo(node->MName.AsString, position, orientation, scale, meshIndex, children);
    }

    static unsafe SubMesh? AppendMesh(
        AssimpMesh* mesh,
        List<Vertex> vertices,
        List<Vector2> texCoord1,
        List<uint> indices)
    {
        if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0)
        {
            return null;
        }

        var baseVertex = (uint)vertices.Count;
        var indexStart = (uint)indices.Count;

        var uv1 = mesh->MTextureCoords[1];

        for (uint i = 0; i < mesh->MNumVertices; i++)
        {
            vertices.Add(ReadVertex(mesh, i));
            texCoord1.Add(uv1 is null ? default : new Vector2(uv1[i].X, uv1[i].Y));
        }

        for (uint f = 0; f < mesh->MNumFaces; f++)
        {
            var face = mesh->MFaces[f];
            for (uint i = 0; i < face.MNumIndices; i++)
            {
                indices.Add(baseVertex + face.MIndices[i]);
            }
        }

        var box = mesh->MAABB;
        var bounds = new Bounds(
            new Vector3(box.Min.X, box.Min.Y, box.Min.Z),
            new Vector3(box.Max.X, box.Max.Y, box.Max.Z));

        return new SubMesh(
            indexStart,
            (uint)indices.Count - indexStart,
            (int)mesh->MMaterialIndex,
            bounds);
    }

    static unsafe Vertex ReadVertex(Silk.NET.Assimp.Mesh* mesh, uint index)
    {
        var normal = mesh->MNormals is null ? Vector3.UnitY : mesh->MNormals[index];
        var colors = mesh->MColors[0];
        var color = colors is null ? Vector3.One : new Vector3(colors[index].X, colors[index].Y, colors[index].Z);
        var uv = mesh->MTextureCoords[0];
        return new Vertex(mesh->MVertices[index], color)
        {
            Normal = normal,
            Uv = uv is null ? default : new Vector2(uv[index].X, uv[index].Y),
            Tangent = ReadTangent(mesh, index, normal),
        };
    }

    static unsafe Vector4 ReadTangent(Silk.NET.Assimp.Mesh* mesh, uint index, Vector3 normal)
    {
        if (mesh->MTangents is null || mesh->MBitangents is null) return Vector4.Zero;
        var tangent = mesh->MTangents[index];
        var bitangent = mesh->MBitangents[index];
        var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0f ? -1f : 1f;
        return new Vector4(tangent.X, tangent.Y, tangent.Z, handedness);
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
        MeshBlobContent Content,
        IReadOnlyList<FbxMaterialInfo> Materials,
        IReadOnlyList<string> TexturePaths,
        IReadOnlySet<int> SrgbTextures,
        IReadOnlySet<int> GreenFlippedTextures,
        FbxNodeInfo Root);

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
