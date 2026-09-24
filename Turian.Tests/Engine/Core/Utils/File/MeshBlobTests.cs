namespace Turian.Tests;

/// <summary>Tests for the <c>.ammesh</c> container reader and writer.</summary>
public class MeshBlobTests
{
    static MeshBlobContent BuildContent(bool withTexCoord1)
    {
        Vertex[] vertices =
        [
            new(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f))
            {
                Normal = new Vector3(0f, 1f, 0f),
                Uv = new Vector2(0f, 0f),
                Tangent = new Vector4(1f, 0f, 0f, 1f),
            },
            new(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f))
            {
                Normal = new Vector3(0f, 1f, 0f),
                Uv = new Vector2(1f, 0f),
                Tangent = new Vector4(1f, 0f, 0f, 1f),
            },
            new(new Vector3(0f, 2f, 3f), new Vector3(0f, 0f, 1f))
            {
                Normal = new Vector3(0f, 0f, 1f),
                Uv = new Vector2(0f, 1f),
                Tangent = new Vector4(0f, 1f, 0f, -1f),
            },
        ];

        return new MeshBlobContent
        {
            Vertices = vertices,
            TexCoord1 = withTexCoord1
                ?
                [
                    new Vector2(0.25f, 0.5f),
                    new Vector2(0.75f, 0.5f),
                    new Vector2(0.5f, 1f),
                ]
                : [],
            Indices = [0, 1, 2, 2, 1, 0],
            SubMeshes =
            [
                new SubMesh(0, 3, 7, new Bounds(new Vector3(0f, 0f, 0f), new Vector3(1f, 2f, 3f))),
                new SubMesh(3, 3, null, new Bounds(new Vector3(-1f, 0f, 0f), new Vector3(1f, 1f, 1f))),
            ],
            Meshes =
            [
                new MeshBlobMesh("first", 0, 1, new Bounds(new Vector3(0f, 0f, 0f), new Vector3(1f, 2f, 3f))),
                new MeshBlobMesh("second — ünïcode", 1, 1, new Bounds(new Vector3(-1f, 0f, 0f), new Vector3(1f, 1f, 1f))),
            ],
            Bounds = new Bounds(new Vector3(-1f, 0f, 0f), new Vector3(1f, 2f, 3f)),
        };
    }

    static MeshBlob RoundTrip(MeshBlobContent content)
    {
        using var stream = new MemoryStream();
        MeshBlobWriter.Write(stream, content);
        stream.Position = 0;
        return MeshBlob.Read(stream);
    }

    /// <summary>Verifies that vertices survive a write/read round trip unchanged.</summary>
    [Fact]
    public void RoundTrip_PreservesVertices()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(content.Vertices, blob.Vertices);
    }

    /// <summary>Verifies that the index buffer survives a write/read round trip unchanged.</summary>
    [Fact]
    public void RoundTrip_PreservesIndices()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(content.Indices, blob.Indices);
    }

    /// <summary>Verifies that the submesh table, its material slots and its bounds survive.</summary>
    [Fact]
    public void RoundTrip_PreservesSubMeshes()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(2, blob.SubMeshes.Count);
        Assert.Equal(content.SubMeshes[0], blob.SubMeshes[0]);
        Assert.Equal(content.SubMeshes[1], blob.SubMeshes[1]);
        Assert.Equal(7, blob.SubMeshes[0].MaterialIndex);
        Assert.Null(blob.SubMeshes[1].MaterialIndex);
    }

    /// <summary>Verifies that the mesh table, including non-ASCII names, survives.</summary>
    [Fact]
    public void RoundTrip_PreservesMeshes()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(content.Meshes, blob.Meshes);
        Assert.Equal("second — ünïcode", blob.Meshes[1].Name);
    }

    /// <summary>Verifies that the whole-file bounds survive.</summary>
    [Fact]
    public void RoundTrip_PreservesBounds()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(content.Bounds, blob.Bounds);
    }

    /// <summary>Verifies that the optional secondary UV stream survives as a second stream.</summary>
    [Fact]
    public void RoundTrip_PreservesTexCoord1()
    {
        var content = BuildContent(withTexCoord1: true);

        var blob = RoundTrip(content);

        Assert.Equal(2, blob.Streams.Count);
        Assert.Equal(content.TexCoord1, blob.TexCoord1);
        Assert.Equal(
            MeshAttributeSemantic.TexCoord1,
            Assert.Single(blob.Streams[1].Attributes).Semantic);
    }

    /// <summary>Verifies that a source without a secondary UV set writes a single stream.</summary>
    [Fact]
    public void RoundTrip_WithoutTexCoord1_WritesOneStream()
    {
        var content = BuildContent(withTexCoord1: false);

        var blob = RoundTrip(content);

        Assert.Single(blob.Streams);
        Assert.Empty(blob.TexCoord1);
    }

    /// <summary>Verifies that the blob projects onto a model builder the renderer can upload.</summary>
    [Fact]
    public void ToModelBuilder_CarriesGeometryAndSubMeshes()
    {
        var blob = RoundTrip(BuildContent(withTexCoord1: true));

        var builder = blob.ToModelBuilder();

        Assert.Equal(3, builder.Vertices.Length);
        Assert.Equal(6, builder.Indices.Length);
        Assert.Equal(2, builder.SubMeshes.Count);
    }

    /// <summary>Verifies that the interleaved vertex stream declares the glTF 2.0 attributes it holds.</summary>
    [Fact]
    public void VertexStream_DeclaresGltfAttributes()
    {
        var blob = RoundTrip(BuildContent(withTexCoord1: true));

        var names = blob.Streams[0].Attributes.Select(a => a.Semantic.ToGltfName()).ToArray();

        Assert.Equal(["POSITION", "COLOR_0", "NORMAL", "TEXCOORD_0", "TANGENT"], names);
        Assert.All(blob.Streams[0].Attributes, a => Assert.Equal(MeshBlob.ComponentTypeFloat, a.ComponentType));
        Assert.Equal(Vertex.SizeOf(), blob.Streams[0].ByteStride);
    }

    /// <summary>Verifies that every semantic maps to a glTF name and back without loss.</summary>
    [Theory]
    [InlineData(MeshAttributeSemantic.Position, "POSITION")]
    [InlineData(MeshAttributeSemantic.Normal, "NORMAL")]
    [InlineData(MeshAttributeSemantic.Tangent, "TANGENT")]
    [InlineData(MeshAttributeSemantic.TexCoord0, "TEXCOORD_0")]
    [InlineData(MeshAttributeSemantic.TexCoord1, "TEXCOORD_1")]
    [InlineData(MeshAttributeSemantic.Color0, "COLOR_0")]
    public void Semantics_MapBothWaysToGltf(MeshAttributeSemantic semantic, string gltfName)
    {
        Assert.Equal(gltfName, semantic.ToGltfName());
        Assert.True(MeshAttributeSemanticExtensions.TryFromGltfName(gltfName, out var parsed));
        Assert.Equal(semantic, parsed);
    }

    /// <summary>Verifies that component counts map to glTF accessor types.</summary>
    [Theory]
    [InlineData(2u, "VEC2")]
    [InlineData(3u, "VEC3")]
    [InlineData(4u, "VEC4")]
    public void ComponentCounts_MapToGltfAccessorTypes(uint componentCount, string accessorType)
    {
        Assert.Equal(accessorType, MeshAttributeSemanticExtensions.ToGltfAccessorType(componentCount));
    }

    /// <summary>Verifies that a file that is not a mesh blob is rejected.</summary>
    [Fact]
    public void Read_RejectsForeignMagic()
    {
        var data = new byte[32];
        "glTF"u8.CopyTo(data);

        Assert.Throws<InvalidDataException>(() => MeshBlob.Read(data));
    }

    /// <summary>Verifies that an empty blob round-trips without geometry.</summary>
    [Fact]
    public void RoundTrip_EmptyContent()
    {
        var blob = RoundTrip(new MeshBlobContent());

        Assert.Empty(blob.Vertices);
        Assert.Empty(blob.Indices);
        Assert.Empty(blob.SubMeshes);
        Assert.Empty(blob.Meshes);
    }
}
