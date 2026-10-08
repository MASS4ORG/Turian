namespace Turian.Tests;

/// <summary>Verifies native conversion produces standard glTF tangent data for mirrored UVs.</summary>
public class AssimpModelConverterTests
{
    /// <summary>Verifies animation-only exports can omit the meshes collection.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"meshes\":[]}")]
    public void TangentAccessorIndices_WithoutGeometry_ReturnsEmpty(string json) =>
        Assert.Empty(AssimpModelConverter.TangentAccessorIndices(JsonNode.Parse(json)!.AsObject()));

    /// <summary>Verifies hierarchy-only models do not require geometry accessors or tangent repair.</summary>
    [Fact]
    public void ConvertToGlb_HierarchyWithoutMeshes_PreservesNodes()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "hierarchy.fbx");
            var destination = Path.Combine(directory, "hierarchy.glb");
            File.WriteAllText(source, """
                FBXHeaderExtension: {
                    FBXHeaderVersion: 1003
                    FBXVersion: 7400
                }
                Objects: {
                    Model: 1, "Model::Root", "Null" {
                        Version: 232
                        Properties70: {
                            P: "Lcl Translation", "Lcl Translation", "", "A", 1, 2, 3
                        }
                    }
                }
                Connections: {
                    C: "OO", 1, 0
                }
                """);
            AssimpModelConverter.ConvertToGlb(source, destination);
            using var document = JsonDocument.Parse(ModelUtils.LoadJsonFromGlb(destination));
            if (document.RootElement.TryGetProperty("meshes", out var meshes))
                Assert.Empty(meshes.EnumerateArray());
            Assert.Contains(document.RootElement.GetProperty("nodes").EnumerateArray(),
                node => node.TryGetProperty("name", out var name) && name.GetString() == "Root");
            Assert.Empty(GltfModelReader.Load(destination).Vertices);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies the output stores normalized VEC4 tangents with both handedness signs.</summary>
    [Fact]
    public void ConvertToGlb_MirroredUvTangents_ExportsValidHandedness()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "mirrored.obj");
            var destination = Path.Combine(directory, "mirrored.glb");
            File.WriteAllText(source, """
                v 0 0 0
                v 1 0 0
                v 0 1 0
                v 2 0 0
                v 3 0 0
                v 2 1 0
                vt 0 0
                vt 1 0
                vt 0 1
                vt 1 0
                vt 0 0
                vt 1 1
                vn 0 0 1
                o first
                f 1/1/1 2/2/1 3/3/1
                o mirrored
                f 4/4/1 5/5/1 6/6/1
                """);
            AssimpModelConverter.ConvertToGlb(source, destination);
            using var document = JsonDocument.Parse(ModelUtils.LoadJsonFromGlb(destination));
            var root = document.RootElement;
            foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    var index = primitive.GetProperty("attributes").GetProperty("TANGENT").GetInt32();
                    var tangent = root.GetProperty("accessors")[index];
                    Assert.Equal("VEC4", tangent.GetProperty("type").GetString());
                    Assert.Equal(5126, tangent.GetProperty("componentType").GetInt32());
                }
            var builder = GltfModelReader.Load(destination);
            AssertTangentBasis(builder);
            var vertices = builder.Vertices;
            Assert.Contains(vertices, v => v.Tangent.W == 1);
            Assert.Contains(vertices, v => v.Tangent.W == -1);
            Assert.All(vertices, v => Assert.Equal(1f,
                new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z).Length(), precision: 5));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    static void AssertTangentBasis(ModelBuilder builder)
    {
        for (var index = 0; index < builder.Indices.Length; index += 3)
        {
            var a = builder.Vertices[builder.Indices[index]];
            var b = builder.Vertices[builder.Indices[index + 1]];
            var c = builder.Vertices[builder.Indices[index + 2]];
            var firstEdge = b.Position - a.Position;
            var secondEdge = c.Position - a.Position;
            var firstUv = b.Uv - a.Uv;
            var secondUv = c.Uv - a.Uv;
            var determinant = firstUv.X * secondUv.Y - firstUv.Y * secondUv.X;
            var expected = Vector3.Normalize((secondEdge * firstUv.X - firstEdge * secondUv.X) / determinant);
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = builder.Vertices[builder.Indices[index + corner]];
                var xyz = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
                var reconstructed = Vector3.Normalize(Vector3.Cross(vertex.Normal, xyz) * vertex.Tangent.W);
                Assert.True(Vector3.Dot(expected, reconstructed) > 0.999f,
                    "Tangent handedness must reconstruct the bitangent defined by the exported UV coordinates.");
            }
        }
    }

    /// <summary>Verifies native asset probing uses the portable RID names for supported architectures.</summary>
    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "x64")]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, "x86")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "arm64")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm, "arm")]
    [InlineData(System.Runtime.InteropServices.Architecture.Wasm, "WASM")]
    public void PortableRuntimeIdentifier_MapsArchitectures(System.Runtime.InteropServices.Architecture architecture,
        string expected) => Assert.Equal("linux-" + expected,
        AssimpModelConverter.PortableRuntimeIdentifier("linux", architecture));
}
