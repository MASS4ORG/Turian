namespace Turian.Tests;

/// <summary>Tests for GltfModelImporter.</summary>
public class GltfModelImporterTests
{
    readonly GltfModelImporter importer = new();

    /// <summary>Verifies that IsValid correctly recognises supported and unsupported extensions.</summary>
    [Theory]
    [InlineData("model.gltf", true)]
    [InlineData("model.glb", true)]
    [InlineData("model.GLTF", true)]
    [InlineData("model.GLB", true)]
    [InlineData("model.obj", false)]
    [InlineData("model.fbx", false)]
    [InlineData("model.dae", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValid_ReturnsExpectedResult(string filePath, bool expected)
    {
        Assert.Equal(expected, importer.IsValid(filePath));
    }

    /// <summary>Verifies that CreateAsset sets the format string from the file extension.</summary>
    [Theory]
    [InlineData("model.gltf", "gltf")]
    [InlineData("model.glb", "glb")]
    [InlineData("model.GLTF", "gltf")]
    public void CreateAsset_SetsFormatFromExtension(string filePath, string expectedFormat)
    {
        var asset = importer.CreateAsset(filePath);

        var modelAsset = Assert.IsType<ModelImportAsset>(asset);
        Assert.Equal(expectedFormat, modelAsset.ImportSettings.Format);
    }

    /// <summary>Verifies that CreateAsset stores the source file path.</summary>
    [Fact]
    public void CreateAsset_SetsRelativePath()
    {
        var asset = importer.CreateAsset("assets/scene.gltf");

        Assert.Equal("assets/scene.gltf", asset.RelativePath);
    }

    /// <summary>Verifies that newly created assets default to importing meshes.</summary>
    [Fact]
    public void CreateAsset_DefaultsImportMeshesTrue()
    {
        var asset = importer.CreateAsset("model.gltf");

        var modelAsset = Assert.IsType<ModelImportAsset>(asset);
        Assert.True(modelAsset.ImportSettings.ImportMeshes);
    }

    /// <summary>Registers every requested path under a deterministic id and records its color-space settings.</summary>
    sealed class RecordingImportContext : IAssetImportContext
    {
        public Dictionary<string, (bool IsSrgb, bool FlipGreenChannel)> Configured { get; } = [];

        public Guid EnsureAsset(string absolutePath) => AssetIdFactory.Derive(Guid.Empty, absolutePath);

        public void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel) =>
            Configured[absolutePath] = (isSrgb, flipGreenChannel);
    }

    /// <summary>
    /// Verifies that images stored as files are referenced by their own asset ids, tagged with the
    /// color space their material role implies, and that each glTF material becomes a child.
    /// </summary>
    [Fact]
    public void CreateChildAssets_BindsExternalImagesAndEmitsMaterials()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gltf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var gltfPath = Path.Combine(dir, "test.gltf");
            File.WriteAllText(gltfPath, @"{
                ""asset"":{""version"":""2.0""},
                ""images"":[{""uri"":""color.png""},{""uri"":""normal%20map.png""}],
                ""textures"":[{""source"":0},{""source"":1}],
                ""materials"":[{
                    ""pbrMetallicRoughness"":{
                        ""baseColorFactor"":[1,0.5,0.25,1],
                        ""metallicFactor"":0.3,
                        ""roughnessFactor"":0.7,
                        ""baseColorTexture"":{""index"":0}
                    },
                    ""normalTexture"":{""index"":1},
                    ""emissiveFactor"":[0,1,0]
                }]
            }");

            var parentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var context = new RecordingImportContext();
            var children = importer.CreateChildAssets(parentId, gltfPath, context).ToList();

            Assert.Empty(children.OfType<TextureAsset>());
            var mat = Assert.Single(children.OfType<MaterialAsset>());

            var colorPath = Path.Combine(dir, "color.png");
            var normalPath = Path.Combine(dir, "normal map.png");

            Assert.Equal(AssetIdFactory.Derive(parentId, "material:0"), mat.Id);
            Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), mat.BaseColorFactor);
            Assert.Equal(0.3f, mat.MetallicFactor);
            Assert.Equal(0.7f, mat.RoughnessFactor);
            Assert.Equal(new Vector3(0f, 1f, 0f), mat.EmissiveFactor);

            Assert.Equal(context.EnsureAsset(colorPath), mat.BaseColorTexture!.AssetId);
            Assert.Equal(context.EnsureAsset(normalPath), mat.NormalTexture!.AssetId);

            Assert.Equal((true, false), context.Configured[colorPath]);
            Assert.Equal((false, false), context.Configured[normalPath]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that an embedded image becomes a child texture whose payload is the image's own
    /// bytes, so it decodes like a file rather than as the child's metadata.
    /// </summary>
    [Fact]
    public void CreateChildAssets_EmbeddedImageCarriesItsBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gltf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
            var gltfPath = Path.Combine(dir, "test.gltf");
            File.WriteAllText(gltfPath, $@"{{
                ""asset"":{{""version"":""2.0""}},
                ""images"":[{{""uri"":""data:image/png;base64,{Convert.ToBase64String(png)}""}}],
                ""textures"":[{{""source"":0}}],
                ""materials"":[{{""pbrMetallicRoughness"":{{""baseColorTexture"":{{""index"":0}}}}}}]
            }}");

            var parentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var children = importer.CreateChildAssets(parentId, gltfPath, IAssetImportContext.None).ToList();

            var texture = Assert.Single(children.OfType<TextureAsset>());
            var mat = Assert.Single(children.OfType<MaterialAsset>());

            Assert.Equal(AssetIdFactory.Derive(parentId, "image:0"), texture.Id);
            Assert.Equal(texture.Id, mat.BaseColorTexture!.AssetId);
            Assert.Equal(png, importer.CreateChildAssetBinaryContent(parentId, texture, gltfPath));
            Assert.Null(importer.CreateChildAssetBinaryContent(parentId, mat, gltfPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
