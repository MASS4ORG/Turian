using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Checks startup recovery of missing model artifacts and interrupted child indexing.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetCatalogMigrationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-catalog-migration-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Headless startup repairs missing artifacts and interrupted imports before resolving models.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningRepairsMissingArtifactAndModelChildren(bool staleManifest)
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var source = Path.Combine(assets, "cube.fbx");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cube.fbx"), source);
        var database = new AssetDatabase();
        using (var importer = new AssetImporter(NullLogger.Instance, database, new SettingsService()))
        {
            importer.GenerateMetaFiles(assets);
        }

        var parent = database.GetAssetsSnapshot().Single(static record => record.ParentAssetId == Guid.Empty);
        var childIds = database.GetChildAssets(parent.AssetId).Select(static record => record.AssetId).Order().ToArray();
        Assert.NotEmpty(childIds);
        var directory = Path.GetDirectoryName(parent.ResolveContentPath())!;
        var manifestPath = Path.Combine(directory, "import.json");
        var manifest = Serializer.Load<ImportedAssetManifest>(manifestPath)!;
        var importTime = manifest.ImportedAtUtc;
        var missing = Path.Combine(directory, "missing.bin");
        if (staleManifest)
        {
            File.Delete(parent.ResolveContentPath());
            manifest.ImporterVersion = 1;
            manifest.PrimaryArtifactFileName = "missing.bin";
            manifest.Artifacts = ["missing.bin"];
        }
        manifest.IndexedAssetIds = null;
        Serializer.Save(manifestPath, manifest);
        Directory.Delete(Path.Combine(directory, "children"), recursive: true);
        database.Assets[parent.AssetId].ImportedRelativePath = Path.GetRelativePath(root, missing);
        database.Assets[parent.AssetId].Artifacts = [Path.GetRelativePath(root, missing)];
        database.SaveCatalog(root);

        var settings = new BuildAppSettings { ProjectAbsoluteDir = root };
        using var project = HeadlessProject.Open(settings, NullLogger.Instance, withGraphics: false);
        var repaired = project.Database.Assets[parent.AssetId];
        Assert.EndsWith("primary.glb", repaired.ImportedRelativePath);
        Assert.NotEmpty(GltfModelReader.Load(repaired.ResolveContentPath()).Vertices);
        Assert.Equal(childIds, project.Database.GetChildAssets(parent.AssetId)
            .Select(static record => record.AssetId).Order().ToArray());
        Assert.All(project.Database.GetChildAssets(parent.AssetId), static record => Assert.True(File.Exists(record.ResolveContentPath())));
        Assert.NotNull(Serializer.Load<ImportedAssetManifest>(manifestPath)!.IndexedAssetIds);
        if (!staleManifest)
        {
            Assert.Equal(importTime, Serializer.Load<ImportedAssetManifest>(manifestPath)!.ImportedAtUtc);
        }

        Assert.False(AssetImporter.RepairCachedCatalog(project.Database, settings, NullLogger.Instance));
        var persisted = new AssetDatabase();
        persisted.LoadCatalogFromProject(root);
        Assert.EndsWith("primary.glb", persisted.Assets[parent.AssetId].ImportedRelativePath);
    }

    /// <summary>Resuming a stopped watcher rescans the project instead of leaving an obsolete catalog active.</summary>
    [Fact]
    public void RestartingMonitoringRefreshesRestoredCatalog()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var source = Path.Combine(assets, "text.txt");
        File.WriteAllText(source, "first");
        var settings = new SettingsService();
        var database = new AssetDatabase();
        using var importer = new AssetImporter(NullLogger.Instance, database, settings);
        settings.Set(new AppSettings { ProjectAbsoluteDir = root });
        importer.StartMonitoring();
        importer.StopMonitoring();
        var record = Assert.Single(database.GetAssetsSnapshot());
        var contentPath = record.ResolveContentPath();
        database.Assets[record.AssetId].ImportedRelativePath = ".Cache/stale.txt";
        database.SaveCatalog(root);
        database.LoadCatalogFromProject(root);
        File.WriteAllText(source, "updated");

        importer.StartMonitoring();
        importer.StopMonitoring();

        Assert.Equal(contentPath, database.Assets[record.AssetId].ResolveContentPath());
        Assert.Equal("updated", File.ReadAllText(contentPath));
    }
}
