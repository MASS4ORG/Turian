namespace Turian.Tests;

/// <summary>Tests for <see cref="FbxModelImporter"/> against a small ASCII FBX fixture.</summary>
public class FbxModelImporterTests
{
    static readonly string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cube.fbx");
    static readonly Guid parentAssetId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    readonly FbxModelImporter importer = new();
    readonly RecordingImportContext context = new();

    /// <summary>
    /// Registers every requested path under a deterministic id and records the color-space
    /// settings the importer asked for.
    /// </summary>
    sealed class RecordingImportContext : IAssetImportContext
    {
        public List<string> Requested { get; } = [];

        public Dictionary<string, (bool IsSrgb, bool FlipGreenChannel)> Configured { get; } = [];

        public Guid EnsureAsset(string absolutePath)
        {
            Requested.Add(absolutePath);
            return AssetIdFactory.Derive(parentAssetId, absolutePath);
        }

        public void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel) =>
            Configured[absolutePath] = (isSrgb, flipGreenChannel);
    }

    /// <summary>Verifies that the importer claims FBX files and nothing else.</summary>
    [Theory]
    [InlineData("model.fbx", true)]
    [InlineData("model.FBX", true)]
    [InlineData("model.obj", false)]
    [InlineData("model.gltf", false)]
    [InlineData("", false)]
    public void IsValid_ReturnsExpectedResult(string filePath, bool expected)
    {
        Assert.Equal(expected, importer.IsValid(filePath));
    }

    /// <summary>Verifies that the created metadata records the fbx format.</summary>
    [Fact]
    public void CreateAsset_SetsFbxFormat()
    {
        var asset = Assert.IsType<ModelImportAsset>(importer.CreateAsset("model.fbx"));

        Assert.Equal("fbx", asset.ImportSettings.Format);
    }

    /// <summary>Verifies that the FBX importer, not the generic model importer, owns .fbx.</summary>
    [Fact]
    public void ModelAssetImporter_DoesNotClaimFbx()
    {
        Assert.False(new ModelAssetImporter().IsValid("model.fbx"));
    }

    MeshBlob ImportBlob(string importDirectory)
    {
        var artifacts = importer.ImportToCache(new ModelAsset(), fixturePath, importDirectory);
        return MeshBlob.Load(Path.Combine(importDirectory, artifacts[0]));
    }

    /// <summary>Verifies that the cache artifact is a mesh blob rather than a copy of the source.</summary>
    [Fact]
    public void ImportToCache_WritesMeshBlob()
    {
        var directory = Directory.CreateTempSubdirectory("turian-fbx-");
        try
        {
            var artifacts = importer.ImportToCache(new ModelAsset(), fixturePath, directory.FullName);

            Assert.Equal($"primary{MeshBlob.FileExtension}", Assert.Single(artifacts));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "primary.fbx")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that the two Assimp meshes hanging off one FBX node become one mesh entry with
    /// two submeshes sharing the file's vertex and index buffers.
    /// </summary>
    [Fact]
    public void ImportToCache_GroupsNodeMeshesIntoOneMeshEntry()
    {
        var directory = Directory.CreateTempSubdirectory("turian-fbx-");
        try
        {
            var blob = ImportBlob(directory.FullName);

            var mesh = Assert.Single(blob.Meshes);
            Assert.Equal(0u, mesh.SubMeshStart);
            Assert.Equal(2u, mesh.SubMeshCount);
            Assert.Equal(2, blob.SubMeshes.Count);
            Assert.NotEmpty(blob.Vertices);
            Assert.Equal(12, blob.Indices.Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies that each submesh keeps the material slot its Assimp mesh used.</summary>
    [Fact]
    public void ImportToCache_KeepsMaterialSlotPerSubMesh()
    {
        var directory = Directory.CreateTempSubdirectory("turian-fbx-");
        try
        {
            var blob = ImportBlob(directory.FullName);

            Assert.Equal([0, 1], blob.SubMeshes.Select(subMesh => subMesh.MaterialIndex).ToArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies that submesh index ranges are contiguous and address the shared buffer.</summary>
    [Fact]
    public void ImportToCache_WritesContiguousSubMeshRanges()
    {
        var directory = Directory.CreateTempSubdirectory("turian-fbx-");
        try
        {
            var blob = ImportBlob(directory.FullName);

            Assert.Equal(0u, blob.SubMeshes[0].IndexStart);
            Assert.Equal(blob.SubMeshes[0].IndexCount, blob.SubMeshes[1].IndexStart);
            Assert.Equal(
                (uint)blob.Indices.Length,
                blob.SubMeshes[1].IndexStart + blob.SubMeshes[1].IndexCount);
            Assert.All(blob.Indices, index => Assert.True(index < blob.Vertices.Length));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies that geometry is mirrored onto the engine's Y-down world space.</summary>
    [Fact]
    public void ImportToCache_MirrorsYAxis()
    {
        var directory = Directory.CreateTempSubdirectory("turian-fbx-");
        try
        {
            var blob = ImportBlob(directory.FullName);

            Assert.Contains(blob.Vertices, vertex => vertex.Position.Y > 0f);
            Assert.Contains(blob.Vertices, vertex => vertex.Position.Y < 0f);
            Assert.Equal(-0.5f, blob.Bounds.Min.Y, 3);
            Assert.Equal(0.5f, blob.Bounds.Max.Y, 3);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies that one material, mesh and prefab child is emitted per slot.</summary>
    [Fact]
    public void CreateChildAssets_EmitsMaterialsMeshesAndPrefab()
    {
        var children = importer.CreateChildAssets(parentAssetId, fixturePath, context).ToList();

        Assert.Equal(2, context.Requested.Count);
        Assert.Equal(2, children.OfType<MaterialAsset>().Count());
        Assert.Single(children.OfType<MeshAsset>());
        Assert.Single(children.OfType<Prefab>());
    }

    /// <summary>Verifies that child ids are derived from the parent so reimports keep references.</summary>
    [Fact]
    public void CreateChildAssets_DerivesDeterministicIds()
    {
        var children = importer.CreateChildAssets(parentAssetId, fixturePath, context).ToList();

        Assert.Equal(
            AssetIdFactory.Derive(parentAssetId, "mesh:0"),
            children.OfType<MeshAsset>().Single().Id);
        Assert.Equal(
            AssetIdFactory.Derive(parentAssetId, "prefab"),
            children.OfType<Prefab>().Single().Id);
        Assert.Equal(
            AssetIdFactory.Derive(parentAssetId, "material:0"),
            children.OfType<MaterialAsset>().First().Id);
    }

    /// <summary>Verifies that the mesh child points back at the model asset and its submesh range.</summary>
    [Fact]
    public void CreateChildAssets_MeshCarriesModelReferenceAndRange()
    {
        var mesh = importer.CreateChildAssets(parentAssetId, fixturePath, context).OfType<MeshAsset>().Single();

        Assert.Equal(parentAssetId, mesh.Model?.AssetId);
        Assert.Equal(0u, mesh.SubMeshStart);
        Assert.Equal(2u, mesh.SubMeshCount);
    }

    /// <summary>Verifies that texture paths resolve against the model's directory, separators normalised.</summary>
    [Fact]
    public void CreateChildAssets_ResolvesTexturePathsAgainstTheModelDirectory()
    {
        _ = importer.CreateChildAssets(parentAssetId, fixturePath, context).ToList();

        var expected = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(fixturePath)!, "Textures", "red_BaseColor.dds"));

        Assert.Contains(expected, context.Requested);
        Assert.All(context.Requested, path => Assert.True(Path.IsPathRooted(path)));
    }

    /// <summary>Verifies that textures are bound to their own asset rather than to a child of the model.</summary>
    [Fact]
    public void CreateChildAssets_EmitsNoTextureChildren()
    {
        var children = importer.CreateChildAssets(parentAssetId, fixturePath, context).ToList();

        Assert.Empty(children.OfType<TextureAsset>());
        Assert.NotEmpty(context.Requested);
    }

    /// <summary>Verifies that the legacy diffuse slot becomes the material's base color texture.</summary>
    [Fact]
    public void CreateChildAssets_MapsDiffuseSlotToBaseColor()
    {
        var material = importer.CreateChildAssets(parentAssetId, fixturePath, context)
            .OfType<MaterialAsset>()
            .First();

        var baseColorPath = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(fixturePath)!, "Textures", "red_BaseColor.dds"));

        Assert.Equal(AssetIdFactory.Derive(parentAssetId, baseColorPath), material.BaseColorTexture!.AssetId);
        Assert.True(context.Configured[baseColorPath].IsSrgb);
    }

    /// <summary>Verifies that the prefab child stores a node hierarchy rather than asset metadata.</summary>
    [Fact]
    public void CreateChildAssetContent_ProducesNodeHierarchy()
    {
        var prefab = importer.CreateChildAssets(parentAssetId, fixturePath, context).OfType<Prefab>().Single();

        var json = importer.CreateChildAssetContent(parentAssetId, prefab, fixturePath);

        Assert.NotNull(json);
        var root = Serializer.LoadData<Node>(json);
        Assert.NotNull(root);
        Assert.Equal("cube", root.Name);
        Assert.Single(root.Children);
    }

    /// <summary>Verifies that the mesh-bearing node carries a model component bound to its mesh asset.</summary>
    [Fact]
    public void CreateChildAssetContent_BindsMeshAssetToMeshNode()
    {
        var prefab = importer.CreateChildAssets(parentAssetId, fixturePath, context).OfType<Prefab>().Single();
        var root = Serializer.LoadData<Node>(importer.CreateChildAssetContent(parentAssetId, prefab, fixturePath)!)!;

        var component = Assert.Single(Node.GetComponentsInChildren<ModelComponent>(root));

        Assert.Equal(AssetIdFactory.Derive(parentAssetId, "mesh:0"), component.Mesh?.AssetId);
        Assert.Null(component.Model);
    }

    /// <summary>Verifies that non-prefab children keep their own serialized metadata as payload.</summary>
    [Fact]
    public void CreateChildAssetContent_ReturnsNullForMaterials()
    {
        var material = importer.CreateChildAssets(parentAssetId, fixturePath, context).OfType<MaterialAsset>().First();

        Assert.Null(importer.CreateChildAssetContent(parentAssetId, material, fixturePath));
    }
}
