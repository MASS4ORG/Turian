namespace Turian.Tests;

/// <summary>
/// Verifies that <see cref="RuntimeAssetLoader.LoadDataAsync"/> gives every caller the same shared
/// <see cref="DataAsset"/> instance — the mechanism that makes a "SOAP variable" actually shared —
/// and that <see cref="IAssetLoader.ReleaseAllNonPersistentData"/> discards it correctly.
/// </summary>
public sealed class RuntimeAssetLoaderDataTests : IDisposable
{
    readonly string projectRoot;
    readonly string assetsRoot;
    readonly AssetDatabase database;
    readonly RuntimeAssetLoader loader;

    /// <summary>Creates a throwaway project on disk and a fresh database singleton.</summary>
    public RuntimeAssetLoaderDataTests()
    {
        TestAssetDatabase.Reset();
        database = new AssetDatabase();
        loader = new RuntimeAssetLoader(database);

        projectRoot = Path.Combine(Path.GetTempPath(), $"turian-dataasset-loader-{Guid.NewGuid():N}");
        assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        if (Directory.Exists(projectRoot))
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Writes a payload and its meta file the way the asset browser's "New" creates one, and registers it.</summary>
    Guid CreateDataAsset<T>(T payload, string fileName) where T : DataAsset
    {
        var assetPath = Path.Combine(assetsRoot, fileName);
        var meta = new DataAssetAsset { RelativePath = assetPath };

        Serializer.Save(assetPath, payload);
        Serializer.Save($"{assetPath}.meta", meta);

        Assert.True(database.RegisterAsset(meta));
        return meta.Id;
    }

    /// <summary>Two callers loading the same id get the exact same object, not two independent copies.</summary>
    [Fact]
    public async Task LoadDataAsyncReturnsTheSameInstanceToEveryCaller()
    {
        var id = CreateDataAsset(new DataAssetTest(), "counter.dataasset");

        var first = await loader.LoadDataAsync(id);
        var second = await loader.LoadDataAsync(id);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    /// <summary>
    /// A mutation made through one reference is visible to a caller that loads the same id afterward —
    /// the behavior "shared-state DataAssets" needs and <c>DataAssetAsset.GetContent</c> alone cannot give,
    /// since that always deserializes a fresh copy.
    /// </summary>
    [Fact]
    public async Task MutationIsVisibleToLaterReaders()
    {
        var id = CreateDataAsset(new DataAssetTest { Int = 1 }, "shared.dataasset");

        var writer = (DataAssetTest)(await loader.LoadDataAsync(id))!;
        writer.Int = 99;

        var reader = (DataAssetTest)(await loader.LoadDataAsync(id))!;

        Assert.Equal(99, reader.Int);
    }

    /// <summary>Nothing is cached before the first load.</summary>
    [Fact]
    public void TryGetLoadedDataIsFalseBeforeLoading()
    {
        var id = CreateDataAsset(new DataAssetTest(), "unloaded.dataasset");

        Assert.False(loader.TryGetLoadedData(id, out var content));
        Assert.Null(content);
    }

    /// <summary>Once loaded, the cached instance is available synchronously.</summary>
    [Fact]
    public async Task TryGetLoadedDataIsTrueAfterLoading()
    {
        var id = CreateDataAsset(new DataAssetTest(), "loaded.dataasset");
        var loaded = await loader.LoadDataAsync(id);

        Assert.True(loader.TryGetLoadedData(id, out var content));
        Assert.Same(loaded, content);
    }

    /// <summary>
    /// Evicting non-persistent content forces the next load to re-read from disk — the mechanism
    /// Play Mode uses to discard whatever a session mutated, since nothing was ever written back.
    /// </summary>
    [Fact]
    public async Task ReleaseAllNonPersistentDataForcesAFreshReadOfAuthoredTypes()
    {
        var id = CreateDataAsset(new DataAssetTest { Int = 1 }, "authored.dataasset");
        var beforeReset = (DataAssetTest)(await loader.LoadDataAsync(id))!;
        beforeReset.Int = 999; // a session mutating shared state; never written to disk

        loader.ReleaseAllNonPersistentData();

        var afterReset = (DataAssetTest)(await loader.LoadDataAsync(id))!;
        Assert.NotSame(beforeReset, afterReset);
        Assert.Equal(1, afterReset.Int); // back to what is actually on disk
    }

    /// <summary>A type declaring <see cref="DataAssetPolicy.Persistent"/> survives the eviction.</summary>
    [Fact]
    public async Task ReleaseAllNonPersistentDataKeepsPersistentTypes()
    {
        var id = CreateDataAsset(new PersistentCounter(), "persistent.dataasset");
        var before = await loader.LoadDataAsync(id);

        loader.ReleaseAllNonPersistentData();

        Assert.True(loader.TryGetLoadedData(id, out var afterReset));
        Assert.Same(before, afterReset);
    }

    /// <summary>
    /// <c>PlayModeService.BuildPlayServices</c> gives every session its own <see cref="RuntimeAssetLoader"/>
    /// instance. This pins down why that isolates DataAsset content across sessions: a mutation made
    /// through one loader's cache never reaches another loader's, even for the exact same asset id and
    /// the same database — which is the actual mechanism issue #144's "leaving Play Mode never changes
    /// an asset" needs, no explicit snapshot/restore step required for the common case.
    /// </summary>
    [Fact]
    public async Task SeparateLoaderInstancesDoNotShareCachedContent()
    {
        var id = CreateDataAsset(new DataAssetTest { Int = 1 }, "cross-session.dataasset");
        var sessionLoader = new RuntimeAssetLoader(database);

        var sessionCopy = (DataAssetTest)(await sessionLoader.LoadDataAsync(id))!;
        sessionCopy.Int = 999; // a Play Mode session mutating shared state

        var editorCopy = (DataAssetTest)(await loader.LoadDataAsync(id))!;

        Assert.NotSame(sessionCopy, editorCopy);
        Assert.Equal(1, editorCopy.Int);
    }

    /// <summary>A minimal <see cref="DataAssetPolicy.Persistent"/> fixture type.</summary>
    [DataAssetPolicy(DataAssetPolicy.Persistent)]
    [TypeId("c0000001-0000-4000-8000-000000000002")]
    public sealed class PersistentCounter : DataAsset
    {
        /// <summary>An arbitrary field, unused beyond existing.</summary>
        public int Value;
    }
}
