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
        if (Directory.Exists(projectRoot))
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Verifies that repeated reads return the same instance without touching the disk.</summary>
    [Fact]
    public void GetContent_ReturnsSameInstanceWithoutIo()
    {
        var first = metadata.GetContent(projectRoot, database);
        File.Delete(sourcePath);

        Assert.NotNull(first);
        Assert.Same(first, metadata.GetContent(projectRoot, database));
    }

    /// <summary>A failed load names the asset's source, is not cached, and a later load retries it.</summary>
    [Fact]
    public async Task Loader_ReportsFailedLoadsAndRetries()
    {
        var metaPath = $"{sourcePath}.meta";
        var valid = File.ReadAllText(metaPath);
        var unknownType = JsonNode.Parse(valid)!.AsObject();
        unknownType[ObjectJsonSerializer<Asset>.TypeIdProperty] = Guid.NewGuid().ToString();
        File.WriteAllText(metaPath, unknownType.ToJsonString());
        var loader = new RuntimeAssetLoader(database);

        var error = await Assert.ThrowsAsync<UnresolvableTypeIdException>(() => loader.LoadAsync<Asset>(metadata.Id));
        Assert.False(string.IsNullOrEmpty(error.SourcePath));
        File.WriteAllText(metaPath, "{");
        await Assert.ThrowsAnyAsync<JsonException>(() => loader.LoadAsync<Asset>(metadata.Id));
        File.WriteAllText(metaPath, valid);

        Assert.NotNull(await loader.LoadAsync<DataAssetAsset>(metadata.Id));
    }

    /// <summary>Verifies that every load of an id through one loader shares one payload.</summary>
    [Fact]
    public async Task Loader_SharesPayloadAcrossReferences()
    {
        var loader = new RuntimeAssetLoader(database);
        var reference = new AssetReference<DataAssetAsset>(metadata.Id);

        var a = (await reference.LoadAsync(loader))?.GetContent(projectRoot, database) as DataAssetTest;
        var b = (await reference.LoadAsync(loader))?.GetContent(projectRoot, database) as DataAssetTest;

        Assert.NotNull(a);
        Assert.Same(a, b);
        a.Int = 42;
        Assert.Equal(42, b!.Int);
    }

    /// <summary>A typed reference retains the legacy id JSON and loads only the requested payload.</summary>
    [Fact]
    public async Task TypedReference_LoadsPayloadAndPreservesAssetIdJson()
    {
        var reference = new DataAssetReference<DataAssetTest>(metadata.Id);
        var json = Serializer.Serialize(reference);
        Assert.Equal(metadata.Id.ToString(), JsonNode.Parse(json)!["AssetId"]!.GetValue<string>());

        var restored = Serializer.LoadData<DataAssetReference<DataAssetTest>>(json)!;
        Assert.Equal(metadata.Id, restored.AssetId);
        Assert.IsType<DataAssetTest>(await restored.LoadContentAsync(new RuntimeAssetLoader(database)));
        Assert.Null(await new DataAssetReference<PlayerSettings>(metadata.Id)
            .LoadContentAsync(new RuntimeAssetLoader(database)));
    }

    /// <summary>Catalog candidates, drops and writes must all check payload, including saved invalid ids.</summary>
    [Fact]
    public void Inspector_RejectsPlayerSettingsForInventoryRule()
    {
        var settingsPath = Path.Combine(projectRoot, "Assets", "PlayerSettings.dataasset");
        Serializer.Save<DataAsset>(settingsPath, new PlayerSettings());
        var settings = new DataAssetAsset { RelativePath = settingsPath };
        File.WriteAllText($"{settingsPath}.meta", Serializer.Serialize(settings));
        Assert.True(database.RegisterAsset(settings));

        var holder = new TypedHolder { Rule = new DataAssetReference<DataAssetTest>(settings.Id) };
        var field = InspectorForms.Build(holder).Sections[0].Fields.Single(f => f.Name == nameof(TypedHolder.Rule));
        var reference = ReferenceField.TryCreate(field)!;
        var picker = new ReferencePicker(database, null!, new RuntimeAssetLoader(database));

        Assert.Equal(typeof(DataAssetTest), reference.DataAssetPayloadType);
        Assert.Equal([metadata.Id], picker.Candidates(reference).Select(c => c.Id));
        Assert.False(picker.Accepts(reference, settings.Id));
        Assert.False(picker.Assign(reference, settings.Id));
        Assert.Equal(settings.Id, holder.Rule.AssetId);
        Assert.Contains("Invalid", picker.DisplayName(reference));
        Assert.True(picker.Assign(reference, metadata.Id));
        Assert.Equal(metadata.Id, holder.Rule.AssetId);

        // A catalog round-trip retains the metadata type, but compatibility still comes
        // from the payload; prefab records cannot become DataAsset candidates.
        database.SaveCatalog(projectRoot);
        database.LoadCatalogFromProject(projectRoot);
        var reopened = new ReferencePicker(database, null!, new RuntimeAssetLoader(database));
        Assert.Equal([metadata.Id], reopened.Candidates(reference).Select(c => c.Id));
        Assert.False(reopened.Accepts(reference, settings.Id));
    }

    /// <summary>Cold and warm typed queries filter by catalog type without hydrating 200 payloads.</summary>
    [Fact]
    public async Task Inspector_TypedQueries_UseIndexedPayloadTypesWithoutLoadingCandidates()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 220; i++)
        {
            var path = Path.Combine(projectRoot, "Assets", $"Settings{i}.dataasset");
            Serializer.Save<DataAsset>(path, new PlayerSettings());
            var asset = new DataAssetAsset { RelativePath = path };
            File.WriteAllText($"{path}.meta", Serializer.Serialize(asset));
            Assert.True(database.RegisterAsset(asset));
            ids.Add(asset.Id);
        }

        var holder = new TypedHolder();
        var field = InspectorForms.Build(holder).Sections[0].Fields.Single(f => f.Name == nameof(TypedHolder.Rule));
        var reference = ReferenceField.TryCreate(field)!;
        var loader = new RuntimeAssetLoader(database);
        var picker = new ReferencePicker(database, null!, loader);

        Assert.Equal([metadata.Id], picker.Candidates(reference).Select(c => c.Id));
        Assert.Equal([metadata.Id], picker.Candidates(reference).Select(c => c.Id));
        Assert.All(ids, id => Assert.False(loader.TryGetLoaded<DataAssetAsset>(id, out _)));
        Assert.False(loader.TryGetLoaded<DataAssetAsset>(metadata.Id, out _));

        Assert.True(picker.Assign(reference, metadata.Id));
        Assert.Same(await holder.Rule.LoadContentAsync(loader), await loader.LoadContentAsync<DataAsset>(metadata.Id));
        Assert.All(ids, id => Assert.False(loader.TryGetLoaded<DataAssetAsset>(id, out _)));
    }

    /// <summary>Typed picker entries without an indexed payload type require a catalog reimport.</summary>
    [Fact]
    public void Inspector_CatalogWithoutPayloadType_RequiresReimport()
    {
        database.SaveCatalog(projectRoot);
        var catalogPath = Path.Combine(projectRoot, ".Cache", "assetCatalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!;
        foreach (var record in catalog["Records"]!.AsArray())
            record!.AsObject().Remove("DataAssetPayloadTypeId");
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        database.LoadCatalogFromProject(projectRoot);

        var holder = new TypedHolder();
        var field = InspectorForms.Build(holder).Sections[0].Fields.Single(f => f.Name == nameof(TypedHolder.Rule));
        var loader = new RuntimeAssetLoader(database);
        var picker = new ReferencePicker(database, null!, loader);
        var reference = ReferenceField.TryCreate(field)!;
        Assert.Empty(picker.Candidates(reference));
        Assert.False(loader.TryGetLoaded<DataAssetAsset>(metadata.Id, out _));

        Assert.True(database.RegisterAsset(metadata));
        Assert.Equal([metadata.Id], picker.Candidates(reference).Select(c => c.Id));
        Assert.False(loader.TryGetLoaded<DataAssetAsset>(metadata.Id, out _));
    }

    /// <summary>An indexed subtype is accepted for both wrapper and direct base-type fields.</summary>
    [Fact]
    public async Task Inspector_IndexedSubtype_AssignsSharedDirectPayload()
    {
        var path = Path.Combine(projectRoot, "Assets", "Derived.dataasset");
        Serializer.Save<DataAsset>(path, new DerivedDataAsset());
        var asset = new DataAssetAsset { RelativePath = path };
        File.WriteAllText($"{path}.meta", Serializer.Serialize(asset));
        Assert.True(database.RegisterAsset(asset));

        var holder = new DirectHolder();
        var field = InspectorForms.Build(holder).Sections[0].Fields.Single(f => f.Name == nameof(DirectHolder.Value));
        var reference = ReferenceField.TryCreate(field)!;
        var loader = new RuntimeAssetLoader(database);
        var picker = new ReferencePicker(database, null!, loader);
        Assert.Contains(picker.Candidates(reference), candidate => candidate.Id == asset.Id);
        Assert.False(loader.TryGetLoaded<DataAssetAsset>(asset.Id, out _));
        Assert.True(picker.Assign(reference, asset.Id));
        Assert.Same(holder.Value, await loader.LoadContentAsync<DataAsset>(asset.Id));
    }

    /// <summary>A derived payload used to exercise assignability of indexed types.</summary>
    [TypeId("a3000000-0000-4000-8000-0000000000f1")]
    public sealed class DerivedDataAsset : DataAssetTest;

    sealed class DirectHolder
    {
        public DataAssetTest? Value { get; set; }
    }

    sealed class TypedHolder
    {
        public DataAssetReference<DataAssetTest> Rule { get; set; } = new();
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

        var played = (DataAssetTest)play!.GetContent(projectRoot, database)!;
        played.Int = 42;

        Assert.Equal(1, ((DataAssetTest)editor!.GetContent(projectRoot, database)!).Int);
        Assert.Equal(1, ((DataAssetTest)DataAsset.LoadContent(sourcePath, database)!).Int);
    }

    /// <summary>Verifies that a reload updates the instance live references hold.</summary>
    [Fact]
    public void Reload_KeepsIdentityAndAppliesNewValues()
    {
        var shared = (DataAssetTest)metadata.GetContent(projectRoot, database)!;
        Serializer.Save<DataAsset>(sourcePath, new DataAssetTest { Id = shared.Id, Int = 7 });

        var changes = new List<string>();
        shared.Changed += (_, member) => changes.Add(member);

        var reloaded = metadata.Reload(projectRoot, database);

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
        var template = (DataAssetTest)metadata.GetContent(projectRoot, database)!;

        var copy = DataAsset.Instantiate(template);
        copy.Int = 99;

        Assert.NotSame(template, copy);
        Assert.NotEqual(template.Id, copy.Id);
        Assert.Equal(1, template.Int);
        Assert.Equal(template.Decimal, copy.Decimal);
    }
}
