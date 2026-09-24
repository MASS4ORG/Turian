namespace Turian.Tests;

/// <summary>
/// Tests for the AssetDatabase class.
/// </summary>
public class AssetDatabaseTests : IDisposable
{
    static readonly object lockObject = new();

    /// <summary>
    /// Initializes a new instance of the AssetDatabaseTests class and resets the singleton.
    /// </summary>
    public AssetDatabaseTests()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
        }
    }

    /// <summary>
    /// Disposes of the tests and resets the singleton.
    /// </summary>
    public void Dispose()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
        }
    }

    void ResetAssetDatabase()
    {
        var field = typeof(AssetDatabase).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(null, null);
        }
    }

    /// <summary>
    /// Verifies that the constructor sets the singleton instance.
    /// </summary>
    [Fact]
    public void Constructor_SetsInstance()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            var db = new AssetDatabase();
            Assert.Same(db, AssetDatabase.Instance);
        }
    }

    /// <summary>
    /// Verifies that attempting to create a second instance of AssetDatabase throws an exception.
    /// </summary>
    [Fact]
    public void Constructor_ThrowsIfAlreadyInitialized()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            _ = new AssetDatabase();
            Assert.Throws<InvalidOperationException>(() => new AssetDatabase());
        }
    }

    /// <summary>
    /// Verifies that TryGetAsset returns false when the asset is missing.
    /// </summary>
    [Fact]
    public void TryGetAsset_ReturnsFalseWhenMissing()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            var db = new AssetDatabase();
            var result = db.TryGetAsset(Guid.NewGuid(), out var record);
            Assert.False(result);
            Assert.Null(record);
        }
    }

    /// <summary>
    /// A project that has never been imported has no catalog at all, which is not an error.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsMissingWhenThereIsNoCatalog()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            using var project = new TempProject();

            Assert.Equal(AssetCatalogLoadStatus.Missing, new AssetDatabase().LoadCatalogFromProject(project.Root));
        }
    }

    /// <summary>
    /// A catalog truncated to zero bytes — what a crash or a forced reboot mid-write leaves behind —
    /// must be reported as damaged rather than read as an empty catalog. Reporting it as empty is
    /// what let a project open "successfully" with every scene missing its contents.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsUnreadableWhenTheCatalogIsTruncated()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            using var project = new TempProject();
            project.WriteCatalog(string.Empty);

            var database = new AssetDatabase();

            Assert.Equal(AssetCatalogLoadStatus.Unreadable, database.LoadCatalogFromProject(project.Root));
            Assert.Empty(database.Assets);
        }
    }

    /// <summary>
    /// Partially written or otherwise malformed JSON is damaged in the same way as a truncated file.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsUnreadableWhenTheCatalogIsMalformed()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            using var project = new TempProject();
            project.WriteCatalog("{\"Records\": [{\"AssetId\":");

            Assert.Equal(AssetCatalogLoadStatus.Unreadable, new AssetDatabase().LoadCatalogFromProject(project.Root));
        }
    }

    /// <summary>
    /// A catalog that parses is reported as loaded, even when it legitimately holds no records.
    /// </summary>
    [Fact]
    public void LoadCatalogFromProject_ReportsLoadedForAnEmptyButValidCatalog()
    {
        lock (lockObject)
        {
            ResetAssetDatabase();
            using var project = new TempProject();
            project.WriteCatalog("{\"Version\":1,\"Records\":[]}");

            Assert.Equal(AssetCatalogLoadStatus.Loaded, new AssetDatabase().LoadCatalogFromProject(project.Root));
        }
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
