namespace Turian.Tests;

/// <summary>Checks unchanged geometry imports and external buffers across loose and packed assets.</summary>
[Collection(SerialTests.Name)]
public sealed class GltfPassThroughTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Geometry stays byte-identical and packaged external buffers resolve by stable asset id.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportAndPackRetainsSourceGeometry(bool external)
    {
        using var project = new Project(external);
        var database = project.Import();
        var record = database.Assets[project.Asset.Id];
        Assert.Equal(File.ReadAllBytes(project.Source), File.ReadAllBytes(record.ResolveContentPath()));
        var output = new OapArchiveBuilder(NullLogger.Instance).Build(project.Root, project.Export);
        var reader = OapReader.OpenFile(output.OapFilePath);
        var entry = reader.FindById(project.Asset.Id)!.Value;
        Assert.Equal(OapAssetType.Mesh, (OapAssetType)entry.AssetType);
        var geometry = GltfModelReader.Read(reader.ReadAsset(entry, verify: true), (index, _) =>
        {
            var bufferId = AssetIdFactory.Derive(project.Asset.Id, $"buffer:{index}");
            Assert.Equal(project.Asset.Id, database.Assets[bufferId].ParentAssetId);
            return reader.ReadAsset(reader.FindById(bufferId)!.Value, verify: true);
        });
        Assert.Equal(new uint[] { 0, 1, 2 }, geometry.Indices);
        Assert.Equal(new Bounds(Vector3.Zero, Vector3.One), Assert.Single(geometry.SubMeshes).Bounds);
        Assert.Equal(4, new GltfModelImporter().Version);
    }

    /// <summary>The runtime resolves external buffers from both asset providers and handles missing buffers.</summary>
    [Fact]
    public void RuntimeLoadsExternalBuffersFromLooseFilesAndOap()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        using var project = new Project(true);
        var database = project.Import();
        try
        {
            Assert.NotNull(project.Asset.GetContent(fixture.Vulkan, database));
            ModelAsset.InvalidateCacheEntry(project.Asset.Id);
            new OapArchiveBuilder(NullLogger.Instance).Build(project.Root, project.Export);
            var runtime = new AssetDatabase();
            runtime.LoadRuntimeCatalog(project.Export);
            Assert.NotNull(project.Asset.GetContent(fixture.Vulkan, runtime));
            ModelAsset.InvalidateCacheEntry(project.Asset.Id);
            runtime.Assets.Remove(AssetIdFactory.Derive(project.Asset.Id, "buffer:0"));
            Assert.Null(project.Asset.GetContent(fixture.Vulkan, runtime));
        }
        finally { ModelAsset.InvalidateCacheEntry(project.Asset.Id); }
    }

    /// <summary>Native authoring files require an explicit interchange export.</summary>
    [Theory]
    [InlineData("file.max")]
    [InlineData("file.blend")]
    public void NativeAuthoringFilesExplainTheExportStep(string path)
    {
        var importer = new ModelAssetImporter();
        Assert.False(importer.IsValid(path));
        var error = Assert.Throws<NotSupportedException>(() =>
            importer.ImportToCache(importer.CreateAsset(path), path, Path.GetTempPath()));
        Assert.Contains("glTF/GLB or FBX", error.Message);
    }

    /// <summary>An unsupported model reports an import failure without rewriting metadata or stopping other assets.</summary>
    [Theory]
    [InlineData(".fbx", true)]
    [InlineData(".stl", true)]
    public void UnsupportedAuthoringSourcePreservesMetadataAndContinuesImport(string extension, bool existingMetadata)
    {
        using var project = new Project(false);
        var source = Path.Combine(project.Root, "Assets", "authoring" + extension);
        File.WriteAllText(source, "native authoring source");
        var asset = new ModelImportAsset { Id = Guid.NewGuid(), RelativePath = source };
        if (existingMetadata) Serializer.Save(source + ".meta", asset);
        var metadata = existingMetadata ? File.ReadAllBytes(source + ".meta") : null;
        var database = project.Import();
        var retained = Assert.IsType<ModelImportAsset>(Asset.Load(source + ".meta"));
        if (existingMetadata)
        {
            Assert.Equal(metadata, File.ReadAllBytes(source + ".meta"));
            Assert.Equal(asset.Id, retained.Id);
        }
        Assert.Equal(3, GltfModelReader.Load(database.Assets[project.Asset.Id].ResolveContentPath()).Vertices.Length);
        var output = new OapArchiveBuilder(NullLogger.Instance).Build(project.Root, project.Export);
        Assert.Null(OapReader.OpenFile(output.OapFilePath).FindById(retained.Id));
    }

    sealed class Project : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("turian-gltf-pass-").FullName;
        internal string Export => Path.Combine(Root, "Export");
        internal string Source { get; }
        internal ModelImportAsset Asset { get; }

        internal Project(bool external)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, "Assets")).FullName;
            var glb = Path.Combine(folder, "triangle.glb");
            GltfGeometryFixture.Save(glb,
                [new(Vector3.Zero, Vector3.One), new(Vector3.UnitX, Vector3.One), new(Vector3.One, Vector3.One)],
                [0, 1, 2]);
            Source = external ? Path.Combine(folder, "triangle.gltf") : glb;
            if (external)
            {
                var bytes = File.ReadAllBytes(glb);
                var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                var json = JsonNode.Parse(bytes.AsSpan(20, jsonLength))!;
                json["buffers"]![0]!["uri"] = "vertex%20data.bin";
                File.WriteAllText(Source, json.ToJsonString());
                File.WriteAllBytes(Path.Combine(folder, "vertex data.bin"), bytes[(28 + jsonLength)..]);
                File.Delete(glb);
            }
            Asset = (ModelImportAsset)new GltfModelImporter().CreateAsset(Source);
            Serializer.Save(Source + ".meta", Asset);
        }

        internal AssetDatabase Import()
        {
            var database = new AssetDatabase();
            using var importer = new AssetImporter(NullLogger.Instance, database, new SettingsService());
            importer.GenerateMetaFiles(Path.Combine(Root, "Assets"));
            return database;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
