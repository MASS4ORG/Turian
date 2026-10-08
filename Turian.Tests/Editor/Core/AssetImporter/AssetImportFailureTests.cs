namespace Turian.Tests;

/// <summary>Checks that converter failures preserve asset identity and do not stop a project scan.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetImportFailureTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-import-failure-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>An unexpected converter exception leaves valid metadata byte-identical while other assets import.</summary>
    [Fact]
    public void ManagedConverterFailurePreservesMetadataAndContinuesScanning()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var failingSource = Path.Combine(assets, "model.failed-model");
        File.WriteAllText(failingSource, "source");
        var model = new ModelImportAsset { Id = Guid.NewGuid(), RelativePath = failingSource };
        Serializer.Save(failingSource + ".meta", model);
        File.AppendAllText(failingSource + ".meta", "\n");
        var metadata = File.ReadAllBytes(failingSource + ".meta");
        var healthySource = Path.Combine(assets, "healthy.txt");
        File.WriteAllText(healthySource, "healthy");
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, new SettingsService());

        importer.GenerateMetaFiles(assets);

        Assert.Equal(metadata, File.ReadAllBytes(failingSource + ".meta"));
        Assert.Equal(model.Id, Asset.Load(failingSource + ".meta")!.Id);
        var healthy = database.GetAssetsSnapshot().Single(record => record.SourceRelativePath.EndsWith("healthy.txt"));
        Assert.Equal("healthy", File.ReadAllText(healthy.ResolveContentPath()));
        Assert.True(File.Exists(Path.Combine(root, ".Cache", "assetCatalog.json")));
    }

    /// <summary>Cancellation stops the scan without being treated as malformed metadata.</summary>
    [Fact]
    public void CanceledImportPreservesMetadataAndStopsScanning()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var source = Path.Combine(assets, "model.failed-model");
        File.WriteAllText(source, "cancel");
        Serializer.Save(source + ".meta", new ModelImportAsset { RelativePath = source });
        File.AppendAllText(source + ".meta", "\n");
        var metadata = File.ReadAllBytes(source + ".meta");
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), new SettingsService());

        Assert.Throws<OperationCanceledException>(() => importer.GenerateMetaFiles(assets));

        Assert.Equal(metadata, File.ReadAllBytes(source + ".meta"));
    }

    /// <summary>A malformed metadata document is repaired independently of managed converter failures.</summary>
    [Fact]
    public void MalformedMetadataIsRepairedForHealthySource()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var source = Path.Combine(assets, "healthy.txt");
        File.WriteAllText(source, "healthy");
        File.WriteAllText(source + ".meta", "{");
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, new SettingsService());

        importer.GenerateMetaFiles(assets);

        var metadata = Assert.IsType<Asset>(Asset.Load(source + ".meta"));
        Assert.Equal("healthy", File.ReadAllText(database.Assets[metadata.Id].ResolveContentPath()));
    }

    /// <summary>A test importer that reports an ordinary managed converter failure.</summary>
    public sealed class FailingModelImporter : IAssetImporter
    {
        /// <inheritdoc/>
        public bool IsValid(string filePath) => filePath.EndsWith(".failed-model", StringComparison.Ordinal);

        /// <inheritdoc/>
        public Asset CreateAsset(string filePath) => new ModelImportAsset { RelativePath = filePath };

        /// <inheritdoc/>
        public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
        {
            if (File.ReadAllText(sourcePath) == "cancel") throw new OperationCanceledException();
            throw new NullReferenceException("Converter could not read the model scene.");
        }
    }
}
