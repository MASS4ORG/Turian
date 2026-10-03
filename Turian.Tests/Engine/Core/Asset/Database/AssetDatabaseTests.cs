namespace Turian.Tests;

/// <summary>
/// Tests for the AssetDatabase class.
/// </summary>
public class AssetDatabaseTests
{
    /// <summary>
    /// Databases are plain instances: two can coexist, and a record registered in one is unknown to the other.
    /// </summary>
    [Fact]
    public void Instances_AreIndependent()
    {
        using var project = new TempProject();
        var path = Path.Combine(project.Root, "Assets", "Stats.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        var first = new AssetDatabase();
        var second = new AssetDatabase();
        var record = new DataAssetAsset { RelativePath = path };

        Assert.True(first.RegisterAsset(record));

        Assert.True(first.TryGetAsset(record.Id, out _));
        Assert.False(second.TryGetAsset(record.Id, out _));
    }

    /// <summary>
    /// Verifies that TryGetAsset returns false when the asset is missing.
    /// </summary>
    [Fact]
    public void TryGetAsset_ReturnsFalseWhenMissing()
    {
        var db = new AssetDatabase();
        var result = db.TryGetAsset(Guid.NewGuid(), out var record);
        Assert.False(result);
        Assert.Null(record);
    }

    /// <summary>
    /// A project that has never been imported has no catalog at all, which is not an error.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsMissingWhenThereIsNoCatalog()
    {
        using var project = new TempProject();

        Assert.Equal(AssetCatalogLoadStatus.Missing, new AssetDatabase().LoadCatalogFromProject(project.Root));
    }

    /// <summary>
    /// A catalog truncated to zero bytes — what a crash or a forced reboot mid-write leaves behind —
    /// must be reported as damaged rather than read as an empty catalog. Reporting it as empty is
    /// what let a project open "successfully" with every scene missing its contents.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsUnreadableWhenTheCatalogIsTruncated()
    {
        using var project = new TempProject();
        project.WriteCatalog(string.Empty);

        var database = new AssetDatabase();

        Assert.Equal(AssetCatalogLoadStatus.Unreadable, database.LoadCatalogFromProject(project.Root));
        Assert.Empty(database.Assets);
    }

    /// <summary>
    /// Partially written or otherwise malformed JSON is damaged in the same way as a truncated file.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsUnreadableWhenTheCatalogIsMalformed()
    {
        using var project = new TempProject();
        project.WriteCatalog("{\"Records\": [{\"AssetId\":");

        Assert.Equal(AssetCatalogLoadStatus.Unreadable, new AssetDatabase().LoadCatalogFromProject(project.Root));
    }

    /// <summary>
    /// A catalog that parses is reported as loaded, even when it legitimately holds no records.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsLoadedForAnEmptyButValidCatalog()
    {
        using var project = new TempProject();
        project.WriteCatalog("{\"Version\":1,\"Records\":[]}");

        Assert.Equal(AssetCatalogLoadStatus.Loaded, new AssetDatabase().LoadCatalogFromProject(project.Root));
    }

    /// <summary>A throwaway project folder with a <c>.Cache</c> directory.</summary>
    sealed class TempProject : IDisposable
    {
        public string Root { get; } =
            Directory.CreateTempSubdirectory("turian-catalog-tests").FullName;

        public void WriteCatalog(string contents)
        {
            var cache = Path.Combine(Root, ".Cache");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "assetCatalog.json"), contents);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a leftover temp folder is not worth failing a test over */ }
        }
    }
}
