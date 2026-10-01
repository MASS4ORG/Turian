namespace Turian.Tests;

/// <summary>Checks that queued watcher events cannot mutate an importer after disposal.</summary>
public sealed class AssetImporterLifetimeTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-import-lifetime-").FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        Directory.Delete(project, recursive: true);
    }

    /// <summary>Live events update imported assets; queued events after disposal leave the cache untouched.</summary>
    [Theory]
    [InlineData(WatcherChangeTypes.Created, false)]
    [InlineData(WatcherChangeTypes.Created, true)]
    [InlineData(WatcherChangeTypes.Changed, false)]
    [InlineData(WatcherChangeTypes.Changed, true)]
    [InlineData(WatcherChangeTypes.Deleted, false)]
    [InlineData(WatcherChangeTypes.Deleted, true)]
    [InlineData(WatcherChangeTypes.Renamed, false)]
    [InlineData(WatcherChangeTypes.Renamed, true)]
    public void WatcherEventsRespectImporterLifetime(WatcherChangeTypes change, bool dispose)
    {
        TestAssetDatabase.Reset();
        var assets = Directory.CreateDirectory(Path.Combine(project, "Assets")).FullName;
        var source = Path.Combine(assets, "Base.dataasset");
        var content = new AssemblyDefinition { Name = "Original" };
        Serializer.Save<DataAsset>(source, content);
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), new SettingsService());
        importer.GenerateMetaFiles(assets);
        var before = SnapshotCache();
        var notifications = 0;
        importer.AssetsChanged += () => notifications++;
        if (dispose) importer.Dispose();

        FileSystemEventArgs notification;
        if (change == WatcherChangeTypes.Renamed)
        {
            File.Move(source, Path.Combine(assets, "Moved.dataasset"));
            notification = new RenamedEventArgs(change, assets, "Moved.dataasset", "Base.dataasset");
        }
        else
        {
            var name = change == WatcherChangeTypes.Created ? "New.dataasset" : "Base.dataasset";
            if (change == WatcherChangeTypes.Deleted) File.Delete(source);
            else
            {
                content.Name = "Changed";
                Serializer.Save<DataAsset>(Path.Combine(assets, name), content);
            }

            notification = new FileSystemEventArgs(change, assets, name);
        }

        typeof(AssetImporter).GetMethod($"OnWatcher{change}", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(importer, [notification]);

        if (dispose) Assert.Equal(before, SnapshotCache());
        else Assert.NotEqual(before, SnapshotCache());
        if (dispose) Assert.Equal(0, notifications);
        else Assert.True(notifications > 0);
    }

    (string Path, string Content)[] SnapshotCache() =>
        [.. Directory.EnumerateFiles(Path.Combine(project, ".Cache"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (path, Convert.ToBase64String(File.ReadAllBytes(path))))];

    /// <summary>Deleting an asset's metadata evicts its cache; source-script events leave imported assets alone.</summary>
    [Theory]
    [InlineData("Base.dataasset.meta", true)]
    [InlineData("Script.cs.meta", false)]
    [InlineData("Script.cs", false)]
    public void LiveDeletionEventsRespectAssetKinds(string name, bool removesAsset)
    {
        TestAssetDatabase.Reset();
        var assets = Directory.CreateDirectory(Path.Combine(project, "Assets")).FullName;
        Serializer.Save<DataAsset>(Path.Combine(assets, "Base.dataasset"), new AssemblyDefinition { Name = "Base" });
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), new SettingsService());
        importer.GenerateMetaFiles(assets);
        var before = SnapshotCache();
        File.Delete(Path.Combine(assets, name));

        typeof(AssetImporter).GetMethod("OnWatcherDeleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(importer, [new FileSystemEventArgs(WatcherChangeTypes.Deleted, assets, name)]);

        if (removesAsset) Assert.NotEqual(before, SnapshotCache());
        else Assert.Equal(before, SnapshotCache());
    }
}
