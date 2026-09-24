namespace Turian.Tests;

/// <summary>Tests for the GLTF parser in ModelUtils.</summary>
public class GltfParserTests
{
    static string MakeGltfJson(float[] positions, uint[]? indices = null, float[]? normals = null, float[]? uvs = null)
    {
        var posBytes = FloatsToBytes(positions);
        var allBytes = new List<byte>(posBytes);

        var bvParts = new List<string> { FormattableString.Invariant($"{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{posBytes.Length}}}") };
        var acParts = new List<string> { FormattableString.Invariant($"{{\"bufferView\":0,\"componentType\":5126,\"count\":{positions.Length / 3},\"type\":\"VEC3\"}}") };
        var attrParts = new List<string> { "\"POSITION\":0" };
        var nextAccessor = 1;

        if (normals is not null)
        {
            var nb = FloatsToBytes(normals);
            bvParts.Add(FormattableString.Invariant($"{{\"buffer\":0,\"byteOffset\":{allBytes.Count},\"byteLength\":{nb.Length}}}"));
            acParts.Add(FormattableString.Invariant($"{{\"bufferView\":{nextAccessor},\"componentType\":5126,\"count\":{normals.Length / 3},\"type\":\"VEC3\"}}"));
            attrParts.Add(FormattableString.Invariant($"\"NORMAL\":{nextAccessor}"));
            allBytes.AddRange(nb);
            nextAccessor++;
        }

        if (uvs is not null)
        {
            var ub = FloatsToBytes(uvs);
            bvParts.Add(FormattableString.Invariant($"{{\"buffer\":0,\"byteOffset\":{allBytes.Count},\"byteLength\":{ub.Length}}}"));
            acParts.Add(FormattableString.Invariant($"{{\"bufferView\":{nextAccessor},\"componentType\":5126,\"count\":{uvs.Length / 2},\"type\":\"VEC2\"}}"));
            attrParts.Add(FormattableString.Invariant($"\"TEXCOORD_0\":{nextAccessor}"));
            allBytes.AddRange(ub);
            nextAccessor++;
        }

        var indexPart = "";
        if (indices is not null)
        {
            var ib = IndicesToBytes(indices);
            bvParts.Add(FormattableString.Invariant($"{{\"buffer\":0,\"byteOffset\":{allBytes.Count},\"byteLength\":{ib.Length}}}"));
            acParts.Add(FormattableString.Invariant($"{{\"bufferView\":{nextAccessor},\"componentType\":5125,\"count\":{indices.Length},\"type\":\"SCALAR\"}}"));
            indexPart = FormattableString.Invariant($",\"indices\":{nextAccessor}");
            allBytes.AddRange(ib);
        }

        var b64 = Convert.ToBase64String([.. allBytes]);
        return string.Create(CultureInfo.InvariantCulture, $@"{{
            ""asset"":{{""version"":""2.0""}},
            ""meshes"":[{{""primitives"":[{{""attributes"":{{{string.Join(",", attrParts)}}}{indexPart}}}]}}],
            ""accessors"":[{string.Join(",", acParts)}],
            ""bufferViews"":[{string.Join(",", bvParts)}],
            ""buffers"":[{{""byteLength"":{allBytes.Count},""uri"":""data:application/octet-stream;base64,{b64}""}}]
        }}");
    }

    static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    static byte[] IndicesToBytes(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    /// <summary>Verifies that parsing an empty JSON object returns an empty model.</summary>
    [Fact]
    public void ParseGltf_EmptyJson_ReturnsEmptyModel()
    {
        var builder = ModelUtils.ParseGltf("{}", null);

        Assert.Empty(builder.Vertices);
        Assert.Empty(builder.Indices);
    }

    /// <summary>Verifies that a triangle mesh with non-indexed geometry produces 3 vertices and 3 indices.</summary>
    [Fact]
    public void ParseGltf_Triangle_ReturnsThreeVerticesAndIndices()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var json = MakeGltfJson(positions);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(3, builder.Vertices.Length);
        Assert.Equal(3, builder.Indices.Length);
    }

    /// <summary>Verifies that vertex positions are read correctly from the position accessor.</summary>
    [Fact]
    public void ParseGltf_Triangle_PositionsCorrect()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var json = MakeGltfJson(positions);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(0f, builder.Vertices[0].Position.X);
        Assert.Equal(1f, builder.Vertices[1].Position.X);
        Assert.Equal(1f, builder.Vertices[2].Position.Y);
    }

    /// <summary>Verifies that normals are read from the NORMAL accessor when present.</summary>
    [Fact]
    public void ParseGltf_WithNormals_SetsNormals()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] normals = [0, 0, 1, 0, 0, 1, 0, 0, 1];
        var json = MakeGltfJson(positions, normals: normals);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(1f, builder.Vertices[0].Normal.Z);
        Assert.Equal(1f, builder.Vertices[1].Normal.Z);
    }

    /// <summary>Verifies that UV coordinates are read from TEXCOORD_0 when present.</summary>
    [Fact]
    public void ParseGltf_WithTexCoords_SetsUvs()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] uvs = [0, 0, 1, 0, 0, 1];
        var json = MakeGltfJson(positions, uvs: uvs);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(0f, builder.Vertices[0].Uv.X);
        Assert.Equal(1f, builder.Vertices[1].Uv.X);
        Assert.Equal(1f, builder.Vertices[2].Uv.Y);
    }

    /// <summary>Verifies that indexed geometry deduplicates shared vertices correctly.</summary>
    [Fact]
    public void ParseGltf_WithIndices_DeduplicatesSharedVertices()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var json = MakeGltfJson(positions, indices);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(4, builder.Vertices.Length);
        Assert.Equal(6, builder.Indices.Length);
    }

    /// <summary>Verifies that the default up normal is used when no NORMAL accessor is present.</summary>
    [Fact]
    public void ParseGltf_WithMissingNormals_UsesDefaultUpNormal()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var json = MakeGltfJson(positions);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(new Vector3(0f, 1f, 0f), builder.Vertices[0].Normal);
    }

    /// <summary>Verifies that a primitive emits one submesh covering its index range.</summary>
    [Fact]
    public void ParseGltf_SinglePrimitive_EmitsOneSubMesh()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var json = MakeGltfJson(positions);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Single(builder.SubMeshes);
        Assert.Equal(0u, builder.SubMeshes[0].IndexStart);
        Assert.Equal(3u, builder.SubMeshes[0].IndexCount);
        Assert.Null(builder.SubMeshes[0].MaterialIndex);
    }

    /// <summary>Verifies that the TANGENT attribute is read into Vertex.Tangent.</summary>
    [Fact]
    public void ParseGltf_WithTangents_SetsTangents()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] tangents = [1, 0, 0, 1, 0, 1, 0, -1, 0, 0, 1, 1];
        var json = MakeGltfTangentJson(positions, tangents);

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(new Vector4(1, 0, 0, 1), builder.Vertices[0].Tangent);
        Assert.Equal(-1f, builder.Vertices[1].Tangent.W);
    }

    static string MakeGltfTangentJson(float[] positions, float[] tangents)
    {
        var pos = FloatsToBytes(positions);
        var tan = FloatsToBytes(tangents);
        var all = pos.Concat(tan).ToArray();
        var b64 = Convert.ToBase64String(all);
        return string.Create(CultureInfo.InvariantCulture, $@"{{
            ""asset"":{{""version"":""2.0""}},
            ""meshes"":[{{""primitives"":[{{""attributes"":{{""POSITION"":0,""TANGENT"":1}}}}]}}],
            ""accessors"":[
                {{""bufferView"":0,""componentType"":5126,""count"":3,""type"":""VEC3""}},
                {{""bufferView"":1,""componentType"":5126,""count"":3,""type"":""VEC4""}}
            ],
            ""bufferViews"":[
                {{""buffer"":0,""byteOffset"":0,""byteLength"":{pos.Length}}},
                {{""buffer"":0,""byteOffset"":{pos.Length},""byteLength"":{tan.Length}}}
            ],
            ""buffers"":[{{""byteLength"":{all.Length},""uri"":""data:application/octet-stream;base64,{b64}""}}]
        }}");
    }

    /// <summary>Verifies that the per-primitive material index is captured in the submesh.</summary>
    [Fact]
    public void ParseGltf_PrimitiveWithMaterial_CapturesMaterialIndex()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var pos = FloatsToBytes(positions);
        var b64 = Convert.ToBase64String(pos);
        var json = string.Create(CultureInfo.InvariantCulture, $@"{{
            ""asset"":{{""version"":""2.0""}},
            ""meshes"":[{{""primitives"":[{{""attributes"":{{""POSITION"":0}},""material"":2}}]}}],
            ""accessors"":[{{""bufferView"":0,""componentType"":5126,""count"":3,""type"":""VEC3""}}],
            ""bufferViews"":[{{""buffer"":0,""byteOffset"":0,""byteLength"":{pos.Length}}}],
            ""buffers"":[{{""byteLength"":{pos.Length},""uri"":""data:application/octet-stream;base64,{b64}""}}]
        }}");

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Single(builder.SubMeshes);
        Assert.Equal(2, builder.SubMeshes[0].MaterialIndex);
    }

    /// <summary>Verifies that primitives whose POSITION accessor has no bufferView (Draco-compressed) are skipped without throwing.</summary>
    [Fact]
    public void ParseGltf_PositionAccessorWithoutBufferView_SkipsPrimitive()
    {
        var json = @"{
            ""asset"":{""version"":""2.0""},
            ""meshes"":[{""primitives"":[{""attributes"":{""POSITION"":0}}]}],
            ""accessors"":[{""componentType"":5126,""count"":3,""type"":""VEC3""}]
        }";

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Empty(builder.Vertices);
        Assert.Empty(builder.Indices);
    }

    /// <summary>Verifies that multiple meshes are combined into a single model.</summary>
    [Fact]
    public void ParseGltf_MultipleMeshes_CombinesAllVertices()
    {
        float[] pos1 = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] pos2 = [2, 0, 0, 3, 0, 0, 2, 1, 0];
        var b1 = FloatsToBytes(pos1);
        var b2 = FloatsToBytes(pos2);
        var allBytes = b1.Concat(b2).ToArray();
        var b64 = Convert.ToBase64String(allBytes);

        var json = string.Create(CultureInfo.InvariantCulture, $@"{{
            ""asset"":{{""version"":""2.0""}},
            ""meshes"":[
                {{""primitives"":[{{""attributes"":{{""POSITION"":0}}}}]}},
                {{""primitives"":[{{""attributes"":{{""POSITION"":1}}}}]}}
            ],
            ""accessors"":[
                {{""bufferView"":0,""componentType"":5126,""count"":3,""type"":""VEC3""}},
                {{""bufferView"":1,""componentType"":5126,""count"":3,""type"":""VEC3""}}
            ],
            ""bufferViews"":[
                {{""buffer"":0,""byteOffset"":0,""byteLength"":{b1.Length}}},
                {{""buffer"":0,""byteOffset"":{b1.Length},""byteLength"":{b2.Length}}}
            ],
            ""buffers"":[{{""byteLength"":{allBytes.Length},""uri"":""data:application/octet-stream;base64,{b64}""}}]
        }}");

        var builder = ModelUtils.ParseGltf(json, null);

        Assert.Equal(6, builder.Vertices.Length);
        Assert.Equal(6, builder.Indices.Length);
    }
}
