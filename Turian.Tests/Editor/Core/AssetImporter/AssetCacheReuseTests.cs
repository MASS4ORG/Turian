namespace Turian.Tests;

/// <summary>Checks import and index reuse across separate editor lifetimes.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetCacheReuseTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-asset-reuse-").FullName;

    /// <summary>Opts the project out of the renderer brick, whose packages every scan would otherwise restore.</summary>
    public AssetCacheReuseTests() => ProjectPackages.OptOutOfDefault(root, ProjectPackages.RenderBrick);

    /// <summary>Removes the isolated project and its cache.</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>Reopening preserves artifacts and indexes; changed and missing files refresh the affected asset.</summary>
    [Fact]
    public void ReopeningReusesCachedArtifactsAndIndexes()
    {
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "sample.txt");
        File.WriteAllText(source, "first content");
        Scan();
        var manifestPath = Directory.GetFiles(Path.Combine(root, ".Cache", "Assets"), "import.json",
            SearchOption.AllDirectories).Single();
        var original = Serializer.Load<ImportedAssetManifest>(manifestPath)!;
        var artifact = Path.Combine(Path.GetDirectoryName(manifestPath)!, original.PrimaryArtifactFileName);
        var artifactTime = File.GetLastWriteTimeUtc(artifact);
        var progress = Substitute.For<IProgressSink>();
        var indexing = Substitute.For<IProgressScope>();
        progress.BeginChild(BackgroundTaskKind.Import, "Indexing assets").Returns(indexing);
        Scan(progress);
        indexing.Received().Units(0, 0);
        Assert.Equal(original.ImportedAtUtc, Serializer.Load<ImportedAssetManifest>(manifestPath)!.ImportedAtUtc);
        Assert.Equal(artifactTime, File.GetLastWriteTimeUtc(artifact));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        Scan();
        Assert.Equal(original.ImportedAtUtc, Serializer.Load<ImportedAssetManifest>(manifestPath)!.ImportedAtUtc);
        File.WriteAllText(source, "updated content");
        Scan();
        Assert.Equal("updated content", File.ReadAllText(artifact));
        File.Delete(artifact);
        Scan();
        Assert.Equal("updated content", File.ReadAllText(artifact));
        File.Delete(Path.Combine(root, ".Cache", "assetCatalog.json"));
        Scan();
        Assert.NotEmpty(Serializer.Load<ImportedAssetManifest>(manifestPath)!.IndexedAssetIds!);
    }

    /// <summary>Cached model children survive reopening and a missing child is regenerated without rebaking the model.</summary>
    [Fact]
    public void ReopeningPreservesModelChildrenAndRepairsMissingContent()
    {
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cube.fbx"), Path.Combine(assets, "cube.fbx"));
        var first = Scan();
        var parent = first.GetAssetsSnapshot().Single(static record => record.ParentAssetId == Guid.Empty);
        var children = first.GetChildAssets(parent.AssetId).ToArray();
        Assert.NotEmpty(children);
        var content = children.First(static record => record.AssetTypeName == typeof(Prefab).FullName).ResolveContentPath();
        var contentTime = File.GetLastWriteTimeUtc(content);
        var reopened = Scan();
        Assert.Equal(children.Length, reopened.GetChildAssets(parent.AssetId).Count());
        Assert.Equal(contentTime, File.GetLastWriteTimeUtc(content));
        File.Delete(content);
        var repaired = Scan();
        Assert.True(File.Exists(content));
        Assert.Equal(children.Length, repaired.GetChildAssets(parent.AssetId).Count());
    }

    /// <summary>
    /// A cache whose catalog paths use either separator, as Windows and Linux editors have written them, reopens on
    /// any OS without reimporting or reindexing, and its records resolve with forward slashes.
    /// </summary>
    [Theory]
    [InlineData(@"\\")]
    [InlineData("/")]
    public void ReopeningACatalogWrittenWithEitherSeparatorReusesTheCache(string jsonSeparator)
    {
        var models = Path.Combine(root, "Assets", "Models");
        Directory.CreateDirectory(models);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cube.fbx"), Path.Combine(models, "cube.fbx"));
        Scan();
        var catalogPath = Path.Combine(root, ".Cache", "assetCatalog.json");
        File.WriteAllText(catalogPath, File.ReadAllText(catalogPath).Replace("/", jsonSeparator, StringComparison.Ordinal));
        var imported = Directory.GetFiles(Path.Combine(root, ".Cache", "Assets"), "import.json", SearchOption.AllDirectories)
            .ToDictionary(static path => path, static path => Serializer.Load<ImportedAssetManifest>(path)!.ImportedAtUtc);
        var progress = Substitute.For<IProgressSink>();
        var indexing = Substitute.For<IProgressScope>();
        progress.BeginChild(BackgroundTaskKind.Import, "Indexing assets").Returns(indexing);

        var records = Scan(progress).GetAssetsSnapshot();

        indexing.Received().Units(0, 0);
        Assert.All(imported, static entry =>
            Assert.Equal(entry.Value, Serializer.Load<ImportedAssetManifest>(entry.Key)!.ImportedAtUtc));
        Assert.Contains(records, static record => record.ParentAssetId != Guid.Empty);
        Assert.All(records, static record =>
        {
            string[] paths = [record.SourceRelativePath, record.MetaRelativePath, record.ImportedRelativePath,
                .. record.Artifacts, .. record.TargetArtifacts.Values];
            Assert.All(paths, static path => Assert.DoesNotContain('\\', path));
            Assert.True(File.Exists(record.ResolveContentPath()), record.ImportedRelativePath);
        });
    }

    /// <summary>Malformed cached metadata is replaced so the source can still be imported after reopening.</summary>
    [Fact]
    public void ReopeningRepairsNullMetadata()
    {
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "sample.txt");
        File.WriteAllText(source, "content");
        File.WriteAllText(source + ".meta", "null");
        Assert.Single(Scan().GetAssetsSnapshot());
        Assert.NotNull(Asset.Load(source + ".meta"));
    }

    /// <summary>
    /// A cache cooked by the version-1 <c>.amtex</c> texture importer is cleared on reimport, so the stale artifact,
    /// which sorts before <c>.ktx2</c>, can never be picked as the primary.
    /// </summary>
    [Fact]
    public void ReimportClearsStaleAmtexArtifact()
    {
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "albedo.dds"), DdsFixture.Build("DXT1", 16, 16, 5, 8));
        Scan();
        var manifestPath = Directory.GetFiles(Path.Combine(root, ".Cache", "Assets"), "import.json",
            SearchOption.AllDirectories).Single();
        var directory = Path.GetDirectoryName(manifestPath)!;
        foreach (var file in Directory.GetFiles(directory, "primary*"))
        {
            File.Delete(file);
        }

        var stale = Path.Combine(directory, "primary.pc.amtex");
        File.WriteAllText(stale, "stale");
        var manifest = Serializer.Load<ImportedAssetManifest>(manifestPath)!;
        manifest.ImporterVersion = 1;
        manifest.PrimaryArtifactFileName = Path.GetFileName(stale);
        manifest.Artifacts = [Path.GetFileName(stale)];
        manifest.TargetArtifacts = new() { [TextureBuildTarget.Pc] = Path.GetFileName(stale) };
        Serializer.Save(manifestPath, manifest);
        File.Delete(Path.Combine(root, ".Cache", "assetCatalog.json"));

        var record = Scan().GetAssetsSnapshot().Single(static candidate => candidate.ParentAssetId == Guid.Empty);

        Assert.False(File.Exists(stale));
        Assert.Equal(TextureAssetImporter.ArtifactFileName(TextureBuildTarget.Pc),
            Path.GetFileName(record.ResolveContentPath()));
    }

    AssetDatabase Scan(IProgressSink? progress = null)
    {
        var database = new AssetDatabase();
        database.LoadCatalogFromProject(root);
        var settings = new SettingsService();
        using var importer = new AssetImporter(NullLogger.Instance, database, settings);
        settings.Set(new AppSettings { ProjectAbsoluteDir = root });
        importer.StartMonitoring(progress);
        importer.StopMonitoring();
        return database;
    }
}
