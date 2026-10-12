namespace Turian.Tests;

/// <summary>A built game's catalog, packages, metadata and settings index read through a non-file content root.</summary>
public sealed class PackagedContentRuntimeTests
{
    [TypeId("6f0c3a51-9a43-4f58-9d5e-3c1d0f7b2a10")]
    sealed class Packaged : DataAsset
    {
        public int Value { get; set; }
    }

    readonly Guid dataId = Guid.NewGuid();
    readonly Guid settingsId = Guid.NewGuid();
    readonly MemoryContentDirectory content = new();

    /// <summary>Builds the content of a game exported with one package, one overlay and a settings index.</summary>
    public PackagedContentRuntimeTests()
    {
        var dataType = TypeRegistry.GetIdOrThrow(typeof(Packaged));
        var settingsType = TypeRegistry.GetIdOrThrow(typeof(PlayerSettings));
        var game = new OapWriter();
        game.Add(dataId, Payload($$"""{ "__TypeId": "{{dataType}}", "Id": "{{dataId}}", "Value": 1 }"""), "a.dataasset");
        game.Add(settingsId, Payload($$"""
            { "__TypeId": "{{settingsType}}", "Id": "{{settingsId}}", "ProductName": "Packaged" }
            """), "player.dataasset");
        game.SetManifest("""{"name":"game"}""");
        content.Add("Content/game.oap", game.Serialize());

        var overlay = new OapWriter();
        overlay.Add(dataId, Payload($$"""{ "__TypeId": "{{dataType}}", "Id": "{{dataId}}", "Value": 2 }"""), "a.dataasset");
        overlay.SetManifest("""{"name":"mod","requires":["game"]}""");
        content.Add("Content/overlays/mod.oap", overlay.Serialize());

        var catalog = new AssetCatalog { Version = AssetCatalog.CurrentVersion };
        catalog.Records.Add(Record(dataId, dataType));
        catalog.Records.Add(Record(settingsId, settingsType));
        content.Add(AssetDatabase.RuntimeCatalogPath, Serializer.Serialize(catalog));
        content.Add(ProjectSettingsLoader.IndexFileName, $$"""{ "Settings": ["{{settingsId}}"] }""");
    }

    /// <summary>Data assets resolve through the mounted package and its overlay without touching the disk.</summary>
    [Fact]
    public async Task RuntimeCatalogAndPackagesReadThroughContent()
    {
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(content);

        Assert.Same(content, database.Content);
        Assert.Equal(content.Id, database.Assets[dataId].ProjectRootPath);
        var loaded = await new RuntimeAssetLoader(database).LoadContentAsync<Packaged>(dataId);
        Assert.Equal(2, loaded!.Value);

        Assert.True(database.TryGetAssetProvider(dataId, out var provider));
        Assert.True(provider!.Exists);
        using var reader = new StreamReader(provider.GetAssetStream());
        Assert.Contains("\"Value\": 2", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    /// <summary>A meta file shipped in the content describes its asset in place of the catalog type.</summary>
    [Fact]
    public async Task MetaShippedInContentIsRead()
    {
        content.Add("Assets/a.dataasset.meta", $$"""
            { "__TypeId": "{{TypeRegistry.GetIdOrThrow(typeof(DataAssetAsset))}}", "Id": "{{dataId}}",
              "Labels": ["shipped"] }
            """);
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(content);

        var asset = await new RuntimeAssetLoader(database).LoadAsync<DataAssetAsset>(dataId);

        Assert.Equal(["shipped"], asset!.Labels);
    }

    /// <summary>The settings index at the content root lists the settings a built game uses.</summary>
    [Fact]
    public void SettingsIndexReadsThroughContent()
    {
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(content);
        var settings = new AppSettings { ProjectAbsoluteDir = Path.GetTempPath() };

        ProjectSettingsLoader.Load(settings, new RuntimeAssetLoader(database), content);

        Assert.Equal("Packaged", settings.Get<PlayerSettings>().ProductName);
    }

    /// <summary>A missing or unreadable runtime catalog leaves the database empty.</summary>
    [Fact]
    public void UnreadableCatalogLoadsNothing()
    {
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(new MemoryContentDirectory());
        Assert.Empty(database.Assets);

        content.Add(AssetDatabase.RuntimeCatalogPath, "{ not json");
        database.LoadRuntimeCatalog(content);
        Assert.Empty(database.Assets);
    }

    /// <summary>Whole-content reads fail clearly for missing content.</summary>
    [Fact]
    public void ReadAllFailsForMissingContent()
    {
        Assert.False(content.Exists("missing.txt"));
        Assert.Throws<FileNotFoundException>(() => content.ReadAllText("missing.txt"));
    }

    static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    AssetRecord Record(Guid id, Guid payloadType) => new()
    {
        AssetId = id,
        AssetTypeName = typeof(DataAssetAsset).FullName!,
        DataAssetPayloadTypeId = payloadType,
        SourceRelativePath = "Assets/a.dataasset",
        MetaRelativePath = "Assets/a.dataasset.meta",
        PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(id),
        ImportedRelativePath = "Content/game.oap",
        StorageKind = AssetStorageKind.Oap,
    };
}
