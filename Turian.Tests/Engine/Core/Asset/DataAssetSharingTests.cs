namespace Turian.Tests;

/// <summary>
/// Tests that a DataAsset payload is one shared instance per asset and loader, and that
/// <see cref="DataAsset.Instantiate{T}"/> gives independent copies.
/// </summary>
public sealed class DataAssetSharingTests : IDisposable
{
    readonly string projectRoot;
    readonly string sourcePath;
    readonly AssetDatabase database;
    readonly DataAssetAsset metadata;

    /// <summary>Writes a throwaway project holding one registered <see cref="DataAssetTest"/>.</summary>
    public DataAssetSharingTests()
    {
        TestAssetDatabase.Reset();
        database = new AssetDatabase();

        projectRoot = Path.Combine(Path.GetTempPath(), $"turian-dataasset-{Guid.NewGuid():N}");
        var assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);

        sourcePath = Path.Combine(assetsRoot, "Stats.asset");
        Serializer.Save<DataAsset>(sourcePath, new DataAssetTest { Int = 1 });

        metadata = new DataAssetAsset { RelativePath = sourcePath };
        File.WriteAllText($"{sourcePath}.meta", Serializer.Serialize(metadata));
        Assert.True(database.RegisterAsset(metadata));
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

    /// <summary>Verifies that repeated reads return the same instance without touching the disk.</summary>
    [Fact]
    public void GetContent_ReturnsSameInstanceWithoutIo()
    {
        var first = metadata.GetContent(projectRoot);
        File.Delete(sourcePath);

        Assert.NotNull(first);
        Assert.Same(first, metadata.GetContent(projectRoot));
    }

    /// <summary>Verifies that every load of an id through one loader shares one payload.</summary>
    [Fact]
    public async Task Loader_SharesPayloadAcrossReferences()
    {
        var loader = new RuntimeAssetLoader(database);
        var reference = new AssetReference<DataAssetAsset>(metadata.Id);

        var a = (await reference.LoadAsync(loader))?.GetContent(projectRoot) as DataAssetTest;
        var b = (await reference.LoadAsync(loader))?.GetContent(projectRoot) as DataAssetTest;

        Assert.NotNull(a);
        Assert.Same(a, b);
        a.Int = 42;
        Assert.Equal(42, b!.Int);
    }

    /// <summary>
    /// Verifies that separate loaders never share payloads, which is what keeps a play session's
    /// runtime changes out of the editor and off disk.
    /// </summary>
    [Fact]
    public async Task SeparateLoaders_DoNotSharePayloads()
    {
        var editor = await new RuntimeAssetLoader(database).LoadAsync<DataAssetAsset>(metadata.Id);
        var play = await new RuntimeAssetLoader(database).LoadAsync<DataAssetAsset>(metadata.Id);

        var played = (DataAssetTest)play!.GetContent(projectRoot)!;
        played.Int = 42;

        Assert.Equal(1, ((DataAssetTest)editor!.GetContent(projectRoot)!).Int);
        Assert.Equal(1, ((DataAssetTest)DataAsset.LoadContent(sourcePath)!).Int);
    }

    /// <summary>Verifies that a reload updates the instance live references hold.</summary>
    [Fact]
    public void Reload_KeepsIdentityAndAppliesNewValues()
    {
        var shared = (DataAssetTest)metadata.GetContent(projectRoot)!;
        Serializer.Save<DataAsset>(sourcePath, new DataAssetTest { Id = shared.Id, Int = 7 });

        var changes = new List<string>();
        shared.Changed += (_, member) => changes.Add(member);

        var reloaded = metadata.Reload(projectRoot);

        Assert.Same(shared, reloaded);
        Assert.Equal(7, shared.Int);
        Assert.Equal([string.Empty], changes);
        shared.NotifyChanged(nameof(DataAssetTest.Int));
        Assert.Equal(2, changes.Count);
    }

    /// <summary>Verifies that preloading a label loads exactly the assets carrying it.</summary>
    [Fact]
    public async Task PreloadLabel_LoadsLabelledAssets()
    {
        var labelled = RegisterExtra("Level.asset", ["level-1"]);
        var loader = new RuntimeAssetLoader(database);

        await loader.PreloadLabelAsync("level-1", TestContext.Current.CancellationToken);

        Assert.True(loader.TryGetLoaded<DataAssetAsset>(labelled.Id, out _));
        Assert.False(loader.TryGetLoaded<DataAssetAsset>(metadata.Id, out _));
    }

    DataAssetAsset RegisterExtra(string name, List<string> labels)
    {
        var path = Path.Combine(projectRoot, "Assets", name);
        Serializer.Save<DataAsset>(path, new DataAssetTest());
        var meta = new DataAssetAsset { RelativePath = path, Labels = labels };
        File.WriteAllText($"{path}.meta", Serializer.Serialize(meta));
        Assert.True(database.RegisterAsset(meta));
        return meta;
    }

    /// <summary>Verifies that an instantiated copy has a new id and is independent of its template.</summary>
    [Fact]
    public void Instantiate_ReturnsIndependentCopy()
    {
        var template = (DataAssetTest)metadata.GetContent(projectRoot)!;

        var copy = DataAsset.Instantiate(template);
        copy.Int = 99;

        Assert.NotSame(template, copy);
        Assert.NotEqual(template.Id, copy.Id);
        Assert.Equal(1, template.Int);
        Assert.Equal(template.Decimal, copy.Decimal);
    }
}
