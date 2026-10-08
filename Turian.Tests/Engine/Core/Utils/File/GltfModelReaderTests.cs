using System.Runtime.InteropServices;

namespace Turian.Tests;

/// <summary>Verifies source glTF layouts, primitive identity, and malformed geometry handling.</summary>
public class GltfModelReaderTests
{
    static readonly Vertex[] Triangle =
    [
        new(new Vector3(0, 0, 0), new Vector3(1, 0, 0))
            { Normal = Vector3.UnitY, Uv = Vector2.Zero, Tangent = new Vector4(1, 0, 0, 1) },
        new(new Vector3(1, 0, 0), new Vector3(0, 1, 0))
            { Normal = Vector3.UnitZ, Uv = Vector2.UnitX, Tangent = new Vector4(0, 1, 0, -1) },
        new(new Vector3(0, 2, 3), new Vector3(0, 0, 1))
            { Normal = Vector3.UnitX, Uv = Vector2.UnitY, Tangent = new Vector4(0, 0, 1, 1) },
    ];

    static (JsonObject Json, byte[] Bytes) Packed()
    {
        var stride = (int)Vertex.SizeOf();
        var data = new byte[3 * stride + 12];
        MemoryMarshal.AsBytes(Triangle.AsSpan()).CopyTo(data);
        MemoryMarshal.AsBytes(new uint[] { 0, 1, 2 }.AsSpan()).CopyTo(data.AsSpan(3 * stride));
        var accessors = new JsonArray();
        var attributes = new JsonObject();
        var fields = new[] { "Position", "Color", "Normal", "Uv", "Tangent" };
        var names = new[] { "POSITION", "COLOR_0", "NORMAL", "TEXCOORD_0", "TANGENT" };
        for (var i = 0; i < fields.Length; i++)
        {
            var accessor = MakeAccessor(0, 5126, i == 3 ? "VEC2" : i == 4 ? "VEC4" : "VEC3");
            accessor["byteOffset"] = (int)Marshal.OffsetOf<Vertex>(fields[i]);
            accessors.Add(accessor);
            attributes[names[i]] = i;
        }
        accessors.Add(MakeAccessor(1, 5125, "SCALAR"));
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = data.Length }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteLength"] = 3 * stride, ["byteStride"] = stride },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 3 * stride, ["byteLength"] = 12 }),
            ["accessors"] = accessors,
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = attributes, ["indices"] = 5 }),
            }),
        };
        return (json, data);
    }

    static JsonObject MakeAccessor(int view, int componentType, string type) => new()
    {
        ["bufferView"] = view,
        ["componentType"] = componentType,
        ["count"] = 3,
        ["type"] = type,
    };

    static byte[] Glb(JsonObject json, byte[] binary)
    {
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonLength = (text.Length + 3) & ~3;
        var binLength = (binary.Length + 3) & ~3;
        var result = new byte[28 + jsonLength + binLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4e4f534a);
        result.AsSpan(20, jsonLength).Fill(32);
        text.CopyTo(result, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20 + jsonLength), (uint)binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24 + jsonLength), 0x004e4942);
        binary.CopyTo(result, 28 + jsonLength);
        return result;
    }

    static ModelBuilder Read(JsonObject json, byte[] bytes) => GltfModelReader.Read(Glb(json, bytes));

    /// <summary>Verifies that the packed engine layout retains every attribute without conversion.</summary>
    [Fact]
    public void Read_PackedVertices_PreservesGeometryAndBounds()
    {
        var (json, bytes) = Packed();
        json["accessors"]![0]!["min"] = new JsonArray(0, 0, 0);
        json["accessors"]![0]!["max"] = new JsonArray(1, 2, 3);
        var builder = Read(json, bytes);

        Assert.Equal(Triangle, builder.Vertices);
        Assert.Equal(new uint[] { 0, 1, 2 }, builder.Indices);
        Assert.Equal(new Bounds(Vector3.Zero, new Vector3(1, 2, 3)), Assert.Single(builder.SubMeshes).Bounds);
    }

    /// <summary>Verifies separate tightly packed float attribute arrays retain all source values.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_PlanarFloatAttributes_PreservesGeometry(bool omitColor)
    {
        var (json, source) = Packed();
        var views = new JsonArray();
        var data = new List<byte>();
        for (var i = 0; i < 5; i++)
        {
            var accessor = json["accessors"]![i]!;
            var offset = (int)accessor["byteOffset"]!;
            var size = i == 3 ? 8 : i == 4 ? 16 : 12;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = data.Count, ["byteLength"] = size * 3 });
            for (var vertex = 0; vertex < 3; vertex++)
                data.AddRange(source.AsSpan(vertex * (int)Vertex.SizeOf() + offset, size).ToArray());
            accessor["bufferView"] = i;
            accessor["byteOffset"] = 0;
        }
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = data.Count, ["byteLength"] = 12 });
        data.AddRange(source.AsSpan(source.Length - 12).ToArray());
        json["accessors"]![5]!["bufferView"] = 5;
        json["bufferViews"] = views;
        json["buffers"]![0]!["byteLength"] = data.Count;
        if (omitColor)
            ((JsonObject)json["meshes"]![0]!["primitives"]![0]!["attributes"]!).Remove("COLOR_0");
        var builder = Read(json, [.. data]);
        Assert.Equal(omitColor ? Triangle.Select(v => v with { Color = Vector3.One }) : Triangle, builder.Vertices);
        Assert.Equal(new Bounds(Vector3.Zero, new Vector3(1, 2, 3)), builder.SubMeshes[0].Bounds);
    }

    /// <summary>Verifies that a stream uses the same GLB representation without seeking.</summary>
    [Fact]
    public void Read_Stream_PreservesGeometry()
    {
        var (json, bytes) = Packed();
        using var stream = new MemoryStream(Glb(json, bytes));
        Assert.Equal(Triangle, GltfModelReader.Read(stream).Vertices);
    }

    /// <summary>Verifies loading a decompression stream that does not expose a length or position.</summary>
    [Fact]
    public void Read_NonSeekableStream_PreservesGeometry()
    {
        var (json, bytes) = Packed();
        using var storage = new MemoryStream();
        using (var compressor = new GZipStream(storage, CompressionMode.Compress, leaveOpen: true))
            compressor.Write(Glb(json, bytes));
        storage.Position = 0;
        using var decompressor = new GZipStream(storage, CompressionMode.Decompress);
        Assert.Equal(Triangle, GltfModelReader.Read(decompressor).Vertices);
    }

    /// <summary>Verifies that source vertices stay distinct and primitive index offsets stay local.</summary>
    [Fact]
    public void Read_RepeatedPrimitives_PreservesVerticesMaterialsAndOrder()
    {
        var (json, bytes) = Packed();
        var primitives = (JsonArray)json["meshes"]![0]!["primitives"]!;
        primitives[0]!["material"] = 7;
        var second = primitives[0]!.DeepClone();
        second["material"] = 2;
        primitives.Add(second);
        var builder = Read(json, bytes);

        Assert.Equal(6, builder.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, builder.Indices);
        Assert.Equal(new int?[] { 7, 2 }, builder.SubMeshes.Select(s => s.MaterialIndex));
        Assert.Equal(new uint[] { 0, 3 }, builder.SubMeshes.Select(s => s.IndexStart));
    }

    /// <summary>Verifies that scene traversal duplicates instances and excludes unused meshes.</summary>
    [Fact]
    public void Read_SceneNodes_UsesDepthFirstMeshOrder()
    {
        var (json, bytes) = Packed();
        var second = json["meshes"]![0]!.DeepClone();
        second["primitives"]![0]!["material"] = 5;
        ((JsonArray)json["meshes"]!).Add(second);
        json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        json["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 1, ["children"] = new JsonArray(1, 2) },
            new JsonObject { ["mesh"] = 0 }, new JsonObject { ["mesh"] = 1 });
        var builder = Read(json, bytes);
        Assert.Equal(9, builder.Vertices.Length);
        Assert.Equal(new int?[] { 5, null, 5 }, builder.SubMeshes.Select(s => s.MaterialIndex));
    }

    /// <summary>Verifies external URI resolution and base64 buffers without an import rewrite.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JsonBuffers_ResolvesSourceBytes(bool embedded)
    {
        var (json, bytes) = Packed();
        json["buffers"]![0]!["uri"] = embedded
            ? "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) : "mesh.bin";
        var builder = GltfModelReader.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), (index, uri) =>
        {
            Assert.Equal(0, index);
            Assert.Equal("mesh.bin", uri);
            return bytes;
        });
        Assert.Equal(Triangle, builder.Vertices);
    }

    /// <summary>Verifies file-relative external buffer URIs are decoded when loading unchanged JSON.</summary>
    [Fact]
    public void Load_ExternalBuffer_ResolvesRelativeUri()
    {
        var (json, bytes) = Packed();
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            json["buffers"]![0]!["uri"] = "mesh%20data.bin";
            File.WriteAllBytes(Path.Combine(directory, "mesh data.bin"), bytes);
            var path = Path.Combine(directory, "model.gltf");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal(Triangle, GltfModelReader.Load(path).Vertices);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies unresolved external buffers and truncated buffers produce clear errors.</summary>
    [Fact]
    public void Read_InvalidBuffers_ReportsReason()
    {
        var (json, bytes) = Packed();
        json["buffers"]![0]!["uri"] = "mesh.bin";
        Assert.Throws<InvalidDataException>(() => GltfModelReader.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        json["buffers"]![0]!["uri"] = "data:application/octet-stream,broken";
        Assert.Throws<NotSupportedException>(() => Read(json, bytes));
        ((JsonObject)json["buffers"]![0]!).Remove("uri");
        json["buffers"]![0]!["byteLength"] = bytes.Length + 8;
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
    }

    /// <summary>Verifies cyclic scene references fail before recursion can exhaust the stack.</summary>
    [Fact]
    public void Read_CyclicNodes_RejectsScene()
    {
        var (json, bytes) = Packed();
        json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        json["nodes"] = new JsonArray(new JsonObject { ["children"] = new JsonArray(0) });
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
    }

    /// <summary>Verifies both compact unsigned index representations.</summary>
    [Theory]
    [InlineData(5121, 1)]
    [InlineData(5123, 2)]
    public void Read_CompactIndices_ReadsUnsignedValues(int type, int size)
    {
        var (json, bytes) = Packed();
        var offset = (int)json["bufferViews"]![1]!["byteOffset"]!;
        bytes.AsSpan(offset).Clear();
        bytes[offset + size] = 1;
        bytes[offset + 2 * size] = 2;
        json["accessors"]![5]!["componentType"] = type;
        Assert.Equal(new uint[] { 0, 1, 2 }, Read(json, bytes).Indices);
    }

    /// <summary>Verifies normalized signed and unsigned vertex components from interleaved buffers.</summary>
    [Theory]
    [InlineData(5120, 1, 127)]
    [InlineData(5121, 1, 255)]
    [InlineData(5122, 2, 32767)]
    [InlineData(5123, 2, 65535)]
    public void Read_NormalizedColors_ConvertsIntegerValues(int type, int size, int max)
    {
        var (json, bytes) = Packed();
        var accessor = json["accessors"]![1]!;
        accessor["componentType"] = type;
        accessor["normalized"] = true;
        var offset = (int)accessor["byteOffset"]!;
        for (var i = 0; i < 3; i++)
        {
            var color = bytes.AsSpan(offset + i * (int)Vertex.SizeOf(), 3 * size);
            color.Clear();
            if (size == 1)
                color[0] = (byte)max;
            else
                BinaryPrimitives.WriteUInt16LittleEndian(color, (ushort)max);
        }
        var builder = Read(json, bytes);
        Assert.All(builder.Vertices, v => Assert.Equal(Vector3.UnitX, v.Color));
        Assert.Equal(Triangle.Select(v => v.Position), builder.Vertices.Select(v => v.Position));
    }

    /// <summary>Verifies normalized signed minimum components clamp to negative one.</summary>
    [Theory]
    [InlineData(5120, 1)]
    [InlineData(5122, 2)]
    public void Read_SignedNormalizedNormals_ClampsMinimum(int type, int size)
    {
        var (json, bytes) = Packed();
        var accessor = json["accessors"]![2]!;
        accessor["componentType"] = type;
        accessor["normalized"] = true;
        var offset = (int)accessor["byteOffset"]!;
        for (var i = 0; i < 3; i++)
        {
            var normal = bytes.AsSpan(offset + i * (int)Vertex.SizeOf(), 3 * size);
            normal.Clear();
            if (size == 1)
                normal[0] = 128;
            else
                BinaryPrimitives.WriteInt16LittleEndian(normal, short.MinValue);
        }
        Assert.All(Read(json, bytes).Vertices, v => Assert.Equal(-Vector3.UnitX, v.Normal));
    }

    /// <summary>Verifies float RGBA colors retain RGB in the engine's vertex layout.</summary>
    [Fact]
    public void Read_RgbaColors_PreservesRgb()
    {
        var (json, bytes) = Packed();
        json["accessors"]![1]!["type"] = "VEC4";
        Assert.Equal(Triangle, Read(json, bytes).Vertices);
    }

    /// <summary>Verifies omitted attributes use defaults and non-indexed triangles generate indices.</summary>
    [Fact]
    public void Read_PositionOnly_GeneratesDefaultsAndBounds()
    {
        var (json, bytes) = Packed();
        json["meshes"]![0]!["primitives"]![0] = new JsonObject
        {
            ["attributes"] = new JsonObject { ["POSITION"] = 0 },
        };
        var builder = Read(json, bytes);
        Assert.All(builder.Vertices, v =>
        {
            Assert.Equal(Vector3.One, v.Color);
            Assert.Equal(Vector3.UnitY, v.Normal);
            Assert.Equal(Vector2.Zero, v.Uv);
            Assert.Equal(Vector4.Zero, v.Tangent);
        });
        Assert.Equal(new Bounds(Vector3.Zero, new Vector3(1, 2, 3)), builder.SubMeshes[0].Bounds);
    }

    /// <summary>Verifies invalid geometry is rejected before upload.</summary>
    [Theory]
    [InlineData("index")]
    [InlineData("view")]
    [InlineData("accessor")]
    [InlineData("count")]
    [InlineData("type")]
    [InlineData("triangles")]
    [InlineData("normalizedFloat")]
    [InlineData("normalizedUint")]
    [InlineData("normalizedIndices")]
    public void Read_InvalidGeometry_RejectsData(string invalid)
    {
        var (json, bytes) = Packed();
        switch (invalid)
        {
            case "index": bytes[^4] = 3; break;
            case "view": json["bufferViews"]![0]!["byteLength"] = bytes.Length + 1; break;
            case "accessor": json["accessors"]![0]!["byteOffset"] = bytes.Length; break;
            case "count": json["accessors"]![1]!["count"] = 2; break;
            case "type": json["accessors"]![0]!["componentType"] = 5123; break;
            case "triangles": json["accessors"]![5]!["count"] = 2; break;
            case "normalizedFloat": json["accessors"]![1]!["normalized"] = true; break;
            case "normalizedIndices": json["accessors"]![5]!["normalized"] = true; break;
            case "normalizedUint":
                json["accessors"]![1]!["componentType"] = 5125;
                json["accessors"]![1]!["normalized"] = true;
                break;
        }
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
    }

    /// <summary>Verifies unsupported features identify the reason instead of dropping geometry.</summary>
    [Theory]
    [InlineData("extension")]
    [InlineData("version")]
    [InlineData("mode")]
    [InlineData("draco")]
    public void Read_UnsupportedFeatures_ReportsReason(string unsupported)
    {
        var (json, bytes) = Packed();
        switch (unsupported)
        {
            case "extension": json["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression"); break;
            case "version": json["asset"]!["version"] = "1.0"; break;
            case "mode": json["meshes"]![0]!["primitives"]![0]!["mode"] = 1; break;
            case "draco":
                json["meshes"]![0]!["primitives"]![0]!["extensions"] = new JsonObject
                { ["KHR_draco_mesh_compression"] = new JsonObject() }; break;
        }
        Assert.Throws<NotSupportedException>(() => Read(json, bytes));
    }

    /// <summary>Verifies sparse positions with and without a base buffer view preserve the geometry.</summary>
    [Theory]
    [InlineData(false, 5121, 1)]
    [InlineData(true, 5121, 1)]
    [InlineData(false, 5123, 2)]
    [InlineData(true, 5123, 2)]
    [InlineData(false, 5125, 4)]
    [InlineData(true, 5125, 4)]
    public void Read_SparsePositions_AppliesReplacements(bool baseView, int indexType, int size)
    {
        var (json, bytes) = Packed();
        var values = new byte[36];
        for (var i = 0; i < 3; i++)
            MemoryMarshal.Write(values.AsSpan(i * 12), in Triangle[i].Position);
        var sparseIndexOffset = bytes.Length;
        var indexBytes = new byte[(3 * size + 3) & ~3];
        for (var i = 0; i < 3; i++)
            indexBytes[i * size] = (byte)i;
        bytes = [.. bytes, .. indexBytes, .. values];
        json["buffers"]![0]!["byteLength"] = bytes.Length;
        var views = (JsonArray)json["bufferViews"]!;
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = sparseIndexOffset, ["byteLength"] = 3 * size });
        views.Add(new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = sparseIndexOffset + indexBytes.Length,
            ["byteLength"] = 36
        });
        var position = (JsonObject)json["accessors"]![0]!;
        if (!baseView)
        {
            position.Remove("bufferView");
            position.Remove("byteOffset");
        }
        position["sparse"] = new JsonObject
        {
            ["count"] = 3,
            ["indices"] = new JsonObject { ["bufferView"] = 2, ["componentType"] = indexType },
            ["values"] = new JsonObject { ["bufferView"] = 3 },
        };
        Assert.Equal(Triangle, Read(json, bytes).Vertices);
        bytes[sparseIndexOffset + size] = 0;
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
        position["sparse"]!["count"] = 0;
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
        position["sparse"]!["count"] = 3;
        position["sparse"]!["indices"]!["componentType"] = 5122;
        Assert.Throws<InvalidDataException>(() => Read(json, bytes));
    }

    /// <summary>Verifies malformed GLB headers and chunks are rejected explicitly.</summary>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 0)]
    [InlineData(12, 1)]
    [InlineData(16, 0)]
    public void Read_InvalidContainer_RejectsData(int offset, uint value)
    {
        var (json, bytes) = Packed();
        var glb = Glb(json, bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => GltfModelReader.Read(glb));
    }
}
