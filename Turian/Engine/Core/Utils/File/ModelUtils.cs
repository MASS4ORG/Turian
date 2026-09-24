namespace Turian.Engine.Core;

/// <summary>
/// Utility class for loading and creating 3D models.
/// </summary>
public static partial class ModelUtils
{
    /// <summary>
    /// Loads the content of an embedded resource as a string.
    /// </summary>
    /// <param name="path">The path to the embedded resource.</param>
    /// <param name="type">The type within the same assembly that contains the embedded resource.</param>
    /// <returns>The content of the embedded resource as a string.</returns>
    public static string LoadEmbeddedResource(string path, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        using var s = type.Assembly.GetManifestResourceStream(path);
        if (s is null)
        {
            return string.Empty;
        }

        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Gets the text content of an embedded resource with the specified filename.
    /// </summary>
    /// <param name="filename">The filename of the embedded resource.</param>
    /// <returns>The text content of the embedded resource.</returns>
    public static string GetEmbeddedResourceObjText(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // foreach (var item in assembly.GetManifestResourceNames()) { }
        var resourceName =
            assembly.GetManifestResourceNames().FirstOrDefault(s => s.EndsWith(filename, StringComparison.InvariantCultureIgnoreCase))
            ?? throw new FileNotFoundException(
                $"*** No obj file found with name {filename}\n*** Check that resourceName and try again!  Did you forget to set obj file to Embedded Resource/Do Not Copy?"
            );
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"*** No shader file found at {resourceName}\n*** Check that resourceName and try again!  Did you forget to set glsl file to Embedded Resource/Do Not Copy?"
            );
        using var reader = new StreamReader(stream);
        var result = reader.ReadToEnd();
        return result;
    }

    /// <summary>
    /// Reads the JSON chunk from a GLB (binary glTF) file without decoding the binary buffer.
    /// </summary>
    public static string LoadJsonFromGlb(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20)
            {
                throw new InvalidOperationException("GLB file too small");
            }

            // GLB header: magic (4 bytes: "glTF"), version (4 bytes), length (4 bytes)
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "glTF")
            {
                throw new InvalidOperationException("Invalid GLB magic number");
            }

            var version = BitConverter.ToUInt32(data, 4);
            if (version != 2)
            {
                throw new InvalidOperationException($"Unsupported GLB version: {version}");
            }

            // GLB 12-byte header is followed by chunks. First chunk is always JSON.
            // Chunk header layout: length (4 bytes) + type (4 bytes), then data.
            var jsonChunkLength = BitConverter.ToUInt32(data, 12);
            var jsonChunkType = Encoding.ASCII.GetString(data, 16, 4);
            if (jsonChunkType != "JSON")
            {
                throw new InvalidOperationException("First GLB chunk is not JSON");
            }

            var jsonStart = 20;
            var json = Encoding.UTF8.GetString(data, jsonStart, (int)jsonChunkLength);
            return json;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse GLB file at {path}", ex);
        }
    }

    static byte[] LoadBinaryChunkFromGlb(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20)
            {
                return [];
            }

            // 12-byte GLB header, then JSON chunk header (length + type) at offset 12.
            var jsonChunkLength = BitConverter.ToUInt32(data, 12);
            var jsonChunkStart = 20;
            var binChunkStart = (int)(jsonChunkStart + jsonChunkLength);

            if (data.Length < binChunkStart + 8)
            {
                return [];
            }

            // Read BIN chunk size and type
            var binChunkLength = BitConverter.ToUInt32(data, binChunkStart);
            var binChunkType = Encoding.ASCII.GetString(data, binChunkStart + 4, 4);
            if (binChunkType != "BIN\0")
            {
                return [];
            }

            var binDataStart = binChunkStart + 8;
            var result = new byte[(int)binChunkLength];
            Array.Copy(data, binDataStart, result, 0, (int)binChunkLength);
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a glTF 2.0 file (<c>.gltf</c> or <c>.glb</c>) into a <see cref="ModelBuilder"/>.
    /// </summary>
    /// <param name="path">The path to the glTF file.</param>
    public static ModelBuilder LoadGltfToBuilder(string path)
    {
        var isGlb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        var json = isGlb ? LoadJsonFromGlb(path) : File.ReadAllText(path);

        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException($"Failed to load GLTF JSON from {path}");

        var binaryBuffer = isGlb ? LoadBinaryChunkFromGlb(path) : null;
        return ParseGltf(json, binaryBuffer, Path.GetDirectoryName(path));
    }

    internal static ModelBuilder ParseGltf(string json, byte[]? embeddedBinary, string? basePath = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var buffers = LoadGltfBuffers(root, embeddedBinary, basePath);

        var bufferViews = new List<JsonElement>();
        if (root.TryGetProperty("bufferViews", out var bufferViewsElem))
            foreach (var bv in bufferViewsElem.EnumerateArray()) bufferViews.Add(bv);

        var accessors = new List<JsonElement>();
        if (root.TryGetProperty("accessors", out var accessorsElem))
            foreach (var ac in accessorsElem.EnumerateArray()) accessors.Add(ac);

        var vertices = new List<Vertex>();
        var indicesList = new List<uint>();
        var vertexMap = new Dictionary<Vertex, uint>();
        var subMeshes = new List<SubMesh>();

        if (root.TryGetProperty("meshes", out var meshesElem))
        {
            foreach (var mesh in meshesElem.EnumerateArray())
            {
                if (!mesh.TryGetProperty("primitives", out var prims)) continue;
                foreach (var prim in prims.EnumerateArray())
                {
                    var indexStart = (uint)indicesList.Count;
                    ProcessGltfPrimitive(prim, accessors, bufferViews, buffers, vertices, indicesList, vertexMap);
                    var indexCount = (uint)indicesList.Count - indexStart;
                    if (indexCount == 0) continue;

                    int? materialIndex = prim.TryGetProperty("material", out var matElem)
                        ? matElem.GetInt32() : null;
                    subMeshes.Add(new SubMesh(indexStart, indexCount, materialIndex));
                }
            }
        }

        return new ModelBuilder
        {
            Vertices = [.. vertices],
            Indices = [.. indicesList],
            SubMeshes = subMeshes,
        };
    }

    /// <summary>
    /// Reads the encoded bytes (PNG, JPEG, …) of an image a glTF file embeds, either in a buffer
    /// view or as a <c>data:</c> URI.
    /// </summary>
    /// <param name="path">The path to the glTF file.</param>
    /// <param name="imageIndex">Index into the file's <c>images</c> array.</param>
    /// <returns>The image bytes, or <c>null</c> when the image is an external file or does not exist.</returns>
    public static byte[]? LoadGltfEmbeddedImage(string path, int imageIndex)
    {
        var isGlb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(isGlb ? LoadJsonFromGlb(path) : File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("images", out var images) || imageIndex < 0 || imageIndex >= images.GetArrayLength())
            return null;

        var image = images[imageIndex];
        if (image.TryGetProperty("uri", out var uriElem))
        {
            var uri = uriElem.GetString() ?? string.Empty;
            return uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DecodeDataUri(uri) : null;
        }

        if (!image.TryGetProperty("bufferView", out var viewIndex) || !root.TryGetProperty("bufferViews", out var views))
            return null;

        var view = views[viewIndex.GetInt32()];
        var buffers = LoadGltfBuffers(root, isGlb ? LoadBinaryChunkFromGlb(path) : null, Path.GetDirectoryName(path));
        var buffer = buffers[view.GetProperty("buffer").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var offsetElem) ? offsetElem.GetInt32() : 0;
        return buffer.AsSpan(offset, view.GetProperty("byteLength").GetInt32()).ToArray();
    }

    static byte[] DecodeDataUri(string uri) =>
        Convert.FromBase64String(uri[(uri.IndexOf(',', StringComparison.Ordinal) + 1)..]);

    static List<byte[]> LoadGltfBuffers(JsonElement root, byte[]? embeddedBinary, string? basePath)
    {
        var buffers = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var buffersElem)) return buffers;

        foreach (var buf in buffersElem.EnumerateArray())
        {
            if (buf.TryGetProperty("uri", out var uriElem))
            {
                var uri = uriElem.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    buffers.Add(DecodeDataUri(uri));
                }
                else
                {
                    buffers.Add(File.ReadAllBytes(Path.Combine(basePath ?? string.Empty, uri)));
                }
            }
            else
            {
                buffers.Add(embeddedBinary ?? []);
            }
        }

        return buffers;
    }

    static void ProcessGltfPrimitive(
        JsonElement prim,
        List<JsonElement> accessors,
        List<JsonElement> bufferViews,
        List<byte[]> buffers,
        List<Vertex> vertices,
        List<uint> indicesList,
        Dictionary<Vertex, uint> vertexMap)
    {
        if (!prim.TryGetProperty("attributes", out var attrs)) return;
        if (!attrs.TryGetProperty("POSITION", out var posElem)) return;

        var posAccessor = accessors[posElem.GetInt32()];
        if (!posAccessor.TryGetProperty("bufferView", out _))
        {
            Log.Logger.LogWarning("Skipping glTF primitive without bufferView (likely Draco/extension-compressed)");
            return;
        }

        var positions = ReadGltfAccessorFloats(posAccessor, bufferViews, buffers);

        var normals = Array.Empty<float>();
        if (attrs.TryGetProperty("NORMAL", out var normalElem))
            normals = ReadGltfAccessorFloats(accessors[normalElem.GetInt32()], bufferViews, buffers);

        var uvs = Array.Empty<float>();
        if (attrs.TryGetProperty("TEXCOORD_0", out var uvElem))
            uvs = ReadGltfAccessorFloats(accessors[uvElem.GetInt32()], bufferViews, buffers);

        var tangents = Array.Empty<float>();
        if (attrs.TryGetProperty("TANGENT", out var tanElem))
            tangents = ReadGltfAccessorFloats(accessors[tanElem.GetInt32()], bufferViews, buffers);

        var faceIndices = prim.TryGetProperty("indices", out var indicesElem)
            ? ReadGltfAccessorIndices(accessors[indicesElem.GetInt32()], bufferViews, buffers)
            : [.. Enumerable.Range(0, positions.Length / 3).Select(i => (uint)i)];

        foreach (var srcIndex in faceIndices.Select(i => (int)i))
        {
            var pos = new Vector3(positions[srcIndex * 3], positions[srcIndex * 3 + 1], positions[srcIndex * 3 + 2]);

            var normal = normals.Length >= srcIndex * 3 + 3
                ? new Vector3(normals[srcIndex * 3], normals[srcIndex * 3 + 1], normals[srcIndex * 3 + 2])
                : new Vector3(0f, 1f, 0f);

            var uv = uvs.Length >= srcIndex * 2 + 2
                ? new Vector2(uvs[srcIndex * 2], uvs[srcIndex * 2 + 1])
                : new Vector2(0f, 0f);

            var tangent = tangents.Length >= srcIndex * 4 + 4
                ? new Vector4(
                    tangents[srcIndex * 4],
                    tangents[srcIndex * 4 + 1],
                    tangents[srcIndex * 4 + 2],
                    tangents[srcIndex * 4 + 3])
                : new Vector4(0f, 0f, 0f, 0f);

            var vertex = new Vertex(pos, new Vector3(1f, 1f, 1f)) { Normal = normal, Uv = uv, Tangent = tangent };

            if (!vertexMap.TryGetValue(vertex, out var existingIdx))
            {
                existingIdx = (uint)vertices.Count;
                vertexMap[vertex] = existingIdx;
                vertices.Add(vertex);
            }
            indicesList.Add(existingIdx);
        }
    }

    static float[] ReadGltfAccessorFloats(JsonElement accessor, List<JsonElement> bufferViews, List<byte[]> buffers)
    {
        var bv = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
        var bvByteOffset = bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        var byteStride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;
        var accessorByteOffset = accessor.TryGetProperty("byteOffset", out var abo) ? abo.GetInt32() : 0;
        var totalOffset = bvByteOffset + accessorByteOffset;
        var raw = buffers[bv.GetProperty("buffer").GetInt32()];

        var componentType = accessor.GetProperty("componentType").GetInt32();
        var count = accessor.GetProperty("count").GetInt32();
        var components = (accessor.GetProperty("type").GetString() ?? "") switch { "VEC4" => 4, "VEC3" => 3, "VEC2" => 2, _ => 1 };
        var elementStride = byteStride > 0 ? byteStride : components * 4;

        var floats = new float[count * components];
        if (componentType == 5126) // FLOAT
        {
            for (var i = 0; i < count; ++i)
                for (var c = 0; c < components; ++c)
                    floats[i * components + c] = BitConverter.ToSingle(raw, totalOffset + i * elementStride + c * 4);
        }
        return floats;
    }

    static uint[] ReadGltfAccessorIndices(JsonElement accessor, List<JsonElement> bufferViews, List<byte[]> buffers)
    {
        var bv = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
        var bvByteOffset = bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        var byteStride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;
        var accessorByteOffset = accessor.TryGetProperty("byteOffset", out var abo) ? abo.GetInt32() : 0;
        var totalOffset = bvByteOffset + accessorByteOffset;
        var raw = buffers[bv.GetProperty("buffer").GetInt32()];

        var componentType = accessor.GetProperty("componentType").GetInt32();
        var count = accessor.GetProperty("count").GetInt32();
        var elementSize = componentType switch { 5121 => 1, 5123 => 2, _ => 4 };
        var stride = byteStride > 0 ? byteStride : elementSize;

        var indices = new uint[count];
        for (var i = 0; i < count; ++i)
        {
            var off = totalOffset + i * stride;
            indices[i] = componentType switch
            {
                5121 => raw[off],
                5123 => BitConverter.ToUInt16(raw, off),
                _ => BitConverter.ToUInt32(raw, off),
            };
        }
        return indices;
    }

    /// <summary>
    /// Creates a 3D cube model with 6 faces.
    /// </summary>
    /// <param name="vulkan">The Vulkan instance to use for rendering.</param>
    /// <returns>The created 3D cube model.</returns>
    public static Model CreateCubeModel6(Vulkan vulkan)
    {
        var h = .5f;
        var builder = new ModelBuilder
        {
            Vertices =
            [
                // left face (white)
                new(new(-h, -h, -h), Color.White.Rgb),
                new(new(-h, h, h), Color.White.Rgb),
                new(new(-h, -h, h), Color.White.Rgb),
                new(new(-h, h, -h), Color.White.Rgb),
                // x+ right face (red)
                new(new(h, -h, -h), Color.Red.Rgb),
                new(new(h, h, h), Color.Red.Rgb),
                new(new(h, -h, h), Color.Red.Rgb),
                new(new(h, h, -h), Color.Red.Rgb),
                // y+ top face (green, remember y axis points down)
                new(new(-h, -h, -h), Color.Green.Rgb),
                new(new(h, -h, h), Color.Green.Rgb),
                new(new(-h, -h, h), Color.Green.Rgb),
                new(new(h, -h, -h), Color.Green.Rgb),
                // bottom face (cyan)
                new(new(-h, h, -h), Color.Cyan.Rgb),
                new(new(h, h, h), Color.Cyan.Rgb),
                new(new(-h, h, h), Color.Cyan.Rgb),
                new(new(h, h, -h), Color.Cyan.Rgb),
                // z+ nose face (blue)
                new(new(-h, -h, h), Color.Blue.Rgb),
                new(new(h, h, h), Color.Blue.Rgb),
                new(new(-h, h, h), Color.Blue.Rgb),
                new(new(h, -h, h), Color.Blue.Rgb),
                // tail face (orange)
                new(new(-h, -h, -h), Color.Orange.Rgb),
                new(new(h, h, -h), Color.Orange.Rgb),
                new(new(-h, h, -h), Color.Orange.Rgb),
                new(new(h, -h, -h), Color.Orange.Rgb),
            ],
            Indices =
            [
                0,
                1,
                2,
                0,
                3,
                1,
                4,
                5,
                6,
                4,
                7,
                5,
                8,
                9,
                10,
                8,
                11,
                9,
                12,
                13,
                14,
                12,
                15,
                13,
                16,
                17,
                18,
                16,
                19,
                17,
                20,
                21,
                22,
                20,
                23,
                21
            ]
        };

        return new Model(vulkan, builder);
    }

}
