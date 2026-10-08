namespace Turian.Tests;

/// <summary>Checks native authoring documents remain opaque assets without invoking model conversion.</summary>
[Collection(SerialTests.Name)]
public sealed class NativeAuthoringAssetTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-authoring-").FullName;

    /// <summary>Creates a supported model beside the authoring document to verify continued asset imports.</summary>
    public NativeAuthoringAssetTests()
    {
        var assets = Directory.CreateDirectory(Path.Combine(project, "Assets")).FullName;
        GltfGeometryFixture.Save(Path.Combine(assets, "triangle.glb"),
            [new(Vector3.Zero, Vector3.One), new(Vector3.UnitX, Vector3.One), new(Vector3.One, Vector3.One)],
            [0, 1, 2]);
    }

    /// <summary>Blender and 3ds Max documents retain source bytes and metadata while other models import normally.</summary>
    [Theory]
    [InlineData(".blend", false)]
    [InlineData(".blend", true)]
    [InlineData(".MAX", false)]
    [InlineData(".MAX", true)]
    public void ProjectScanRetainsAuthoringDocuments(string extension, bool typedMetadata)
    {
        var source = Path.Combine(project, "Assets", "authoring" + extension);
        byte[] sourceBytes = [.. "native authoring document"u8];
        File.WriteAllBytes(source, sourceBytes);
        var asset = new ModelImportAsset { RelativePath = source };
        if (typedMetadata) Serializer.Save(source + ".meta", asset);
        var metadata = typedMetadata ? File.ReadAllBytes(source + ".meta") : null;
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, new SettingsService());
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        Assert.False(new ModelAssetImporter().IsValid(source));
        Assert.IsType<GenericAssetImporter>(importer.ImporterFor(source));
        var retained = Assert.IsAssignableFrom<Asset>(Asset.Load(source + ".meta"));
        if (typedMetadata)
        {
            Assert.IsType<ModelImportAsset>(retained);
            Assert.Equal(asset.Id, retained.Id);
            Assert.Equal(metadata, File.ReadAllBytes(source + ".meta"));
        }
        else Assert.IsType<Asset>(retained);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        var record = database.Assets[retained.Id];
        Assert.EndsWith(extension, record.ImportedRelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(record.ResolveContentPath()));
        var geometry = Assert.Single(database.GetAssetsSnapshot(), item =>
            item.SourceRelativePath.EndsWith("triangle.glb", StringComparison.Ordinal));
        Assert.Equal(3, GltfModelReader.Load(geometry.ResolveContentPath()).Vertices.Length);

        VerifyExport(retained.Id);
    }

    void VerifyExport(Guid assetId)
    {
        var output = new OapArchiveBuilder(NullLogger.Instance).BuildPackage(project, Path.Combine(project, "game.oap"));
        var entry = OapReader.OpenFile(output.OapFilePath).FindById(assetId);
        Assert.Null(entry);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);
}
