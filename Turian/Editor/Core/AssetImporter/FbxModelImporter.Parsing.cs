using Silk.NET.Assimp;

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
        var api = assimp.Value;
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

    static unsafe FbxImport Build(AssimpApi api, Scene* scene)
    {
        var texturePaths = new List<string>();
        var textureIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var srgbTextures = new HashSet<int>();
        var greenFlippedTextures = new HashSet<int>();

        int Texture(Material* material, TextureType type, bool isSrgb, bool flipGreen)
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
                Texture(material, TextureType.Diffuse, isSrgb: true, flipGreen: false),
                Texture(material, TextureType.Specular, isSrgb: false, flipGreen: false),
                Texture(material, TextureType.Normals, isSrgb: false, flipGreen: true),
                Texture(material, TextureType.Emissive, isSrgb: true, flipGreen: false)));
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
        Scene* scene,
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
        Mesh* mesh,
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

        var uv0 = mesh->MTextureCoords[0];
        var uv1 = mesh->MTextureCoords[1];
        var colors = mesh->MColors[0];

        for (uint i = 0; i < mesh->MNumVertices; i++)
        {
            var position = Mirror(mesh->MVertices[i]);
            var normal = mesh->MNormals is null ? new Vector3(0f, 1f, 0f) : Mirror(mesh->MNormals[i]);

            var tangent = new Vector4(0f, 0f, 0f, 0f);
            if (mesh->MTangents is not null && mesh->MBitangents is not null)
            {
                var t = Mirror(mesh->MTangents[i]);
                var b = Mirror(mesh->MBitangents[i]);
                var handedness = Vector3.Dot(Vector3.Cross(normal, t), b) < 0f ? -1f : 1f;
                tangent = new Vector4(t.X, t.Y, t.Z, handedness);
            }

            var color = colors is null
                ? new Vector3(1f, 1f, 1f)
                : new Vector3(colors[i].X, colors[i].Y, colors[i].Z);

            vertices.Add(new Vertex(position, color)
            {
                Normal = normal,
                Uv = uv0 is null ? default : new Vector2(uv0[i].X, uv0[i].Y),
                Tangent = tangent,
            });

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
            new Vector3(box.Min.X, -box.Max.Y, box.Min.Z),
            new Vector3(box.Max.X, -box.Min.Y, box.Max.Z));

        return new SubMesh(
            indexStart,
            (uint)indices.Count - indexStart,
            (int)mesh->MMaterialIndex,
            bounds);
    }

    static Vector3 Mirror(Vector3 value) => new(value.X, -value.Y, value.Z);

    static (Vector3 Position, Quaternion Orientation, Vector3 Scale)
        DecomposeTransform(Matrix4x4 assimpTransform)
    {
        // Assimp stores column-vector matrices; System.Numerics composes row vectors.
        var local = Matrix4x4.Transpose(assimpTransform);
        var mirrored = yMirror * local * yMirror;

        if (!Matrix4x4.Decompose(mirrored, out var scale, out var rotation, out var translation))
        {
            return (
                new Vector3(mirrored.M41, mirrored.M42, mirrored.M43),
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
