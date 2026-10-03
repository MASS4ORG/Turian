namespace Turian.Tests;

/// <summary>A fixture tying authored IDs, references, variants, mounted overrides and runtime saves together.</summary>
[Collection(SerialTests.Name)]
public sealed class ContentContractTests : IDisposable
{
    [TypeId("4c373005-0a26-46fc-8bcd-853fe6bfd192")]
    sealed class Definition : DataAsset
    {
        public int Health { get; set; }
        public DataAsset? Related { get; set; }
    }

    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-content-{Guid.NewGuid():N}");
    readonly Guid baseId = Guid.NewGuid();
    readonly Guid variantId = Guid.NewGuid();
    readonly Guid relatedId = Guid.NewGuid();

    /// <summary>Creates an isolated package directory and asset database.</summary>
    public ContentContractTests()
    {
        TestAssetDatabase.Reset();
        Directory.CreateDirectory(Path.Combine(root, "overlays"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        Directory.Delete(root, recursive: true);
    }

    /// <summary>One fixture verifies the content and save contract without modifying authored package bytes.</summary>
    [Fact]
    public async Task OverrideAndResumeKeepAuthoredIdentityAndIgnoreOptionalExtension()
    {
        var typeId = TypeRegistry.GetIdOrThrow(typeof(Definition));
        var baseJson = $$"""
            { "__TypeId": "{{typeId}}", "Id": "{{baseId}}", "Health": 10,
              "Related": { "$ref": "{{relatedId}}" } }
            """;
        var baseWriter = new OapWriter();
        baseWriter.Add(baseId, Encoding.UTF8.GetBytes(baseJson), "base.dataasset");
        baseWriter.Add(relatedId, Encoding.UTF8.GetBytes(
            $$"""{ "__TypeId": "{{typeId}}", "Id": "{{relatedId}}", "Health": 80 }"""), "related.dataasset");
        baseWriter.Add(variantId, Encoding.UTF8.GetBytes(Variant(variantId, baseId, 20)), "variant.dataasset");
        baseWriter.SetManifest("""{"name":"base"}""");
        var basePath = Path.Combine(root, "base.oap");
        baseWriter.WriteToFile(basePath);

        var overlayWriter = new OapWriter();
        overlayWriter.Add(variantId, Encoding.UTF8.GetBytes(Variant(variantId, baseId, 30)), "variant.dataasset");
        overlayWriter.SetManifest("""{"name":"mod","requires":["base"]}""");
        var overlayPath = Path.Combine(root, "overlays", "mod.oap");
        overlayWriter.WriteToFile(overlayPath);

        var database = new AssetDatabase();
        foreach (var id in new[] { baseId, relatedId, variantId })
            database.Assets[id] = new AssetRecord
            {
                AssetId = id,
                AssetTypeName = typeof(DataAssetAsset).FullName!,
                DataAssetPayloadTypeId = typeId,
                ProjectRootPath = root,
                SourceRelativePath = $"Assets/{id}.dataasset",
                ImportedRelativePath = basePath,
                PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(id),
                StorageKind = AssetStorageKind.Oap
            };

        var firstLoader = new RuntimeAssetLoader(database);
        var first = Assert.IsType<Definition>(await firstLoader.LoadContentAsync<DataAsset>(variantId));
        Assert.Equal(variantId, first.Id);
        Assert.Equal(30, first.Health);
        Assert.Equal(relatedId, first.Related?.Id);
        Assert.Same(first, await firstLoader.LoadContentAsync<DataAsset>(variantId));
        Assert.Contains($"\"$ref\": \"{relatedId}\"", Serializer.Serialize<DataAsset>(first), StringComparison.Ordinal);

        var content = new RuntimeSaveContent
        {
            Fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(basePath))),
            Mods = new Dictionary<string, string>
            {
                ["mod"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(overlayPath)))
            }
        };
        var snapshot = new RuntimeSaveSnapshot
        {
            Tick = 42,
            RngState = 1234,
            ContentIds = [variantId],
            State = new JsonObject { ["health"] = 7 },
            Extensions = new Dictionary<string, RuntimeSaveExtension>
            {
                ["mod:optional-feature"] = new() { Data = new JsonObject { ["value"] = true } }
            }
        };
        var saveJson = new RuntimeSave().Write(snapshot, content);
        first.Health = 99;
        var nextLoader = new RuntimeAssetLoader(database);
        var resumed = await new RuntimeSave().ReadAsync(saveJson, content, nextLoader);
        var resumedDefinition = Assert.IsType<Definition>(resumed.Content[variantId]);
        var runtimeCopy = DataAsset.Instantiate((Definition)resumed.Content[variantId]);
        runtimeCopy.Health = resumed.Snapshot.State["health"]!.GetValue<int>();

        Assert.Equal(7, runtimeCopy.Health);
        Assert.Equal(30, resumedDefinition.Health);
        Assert.NotSame(first, resumedDefinition);
        Assert.Same(resumedDefinition, await nextLoader.LoadContentAsync<DataAsset>(variantId));
        Assert.Equal(relatedId, resumedDefinition.Related?.Id);
        Assert.NotEqual(variantId, runtimeCopy.Id);
        Assert.Equal(42, resumed.Snapshot.Tick);
        Assert.Equal(1234UL, resumed.Snapshot.RngState);
        Assert.Equal(["mod:optional-feature"], resumed.UnsupportedOptionalExtensions);
        Assert.True(database.TryGetAssetProvider(baseId, out var provider));
        using var stream = new StreamReader(provider!.GetAssetStream());
        Assert.Equal(baseJson, await stream.ReadToEndAsync(TestContext.Current.CancellationToken));

        var updatedOverlay = new OapWriter();
        updatedOverlay.Add(variantId, Encoding.UTF8.GetBytes(Variant(variantId, baseId, 4000)), "variant.dataasset");
        updatedOverlay.SetManifest("""{"name":"mod","requires":["base"]}""");
        updatedOverlay.WriteToFile(overlayPath);
        firstLoader.Release(variantId);
        await Assert.ThrowsAsync<InvalidDataException>(() => firstLoader.LoadContentAsync<DataAsset>(variantId));
        var updatedSession = new RuntimeAssetLoader(database);
        Assert.Equal(4000, (await updatedSession.LoadContentAsync<Definition>(variantId))!.Health);
        Assert.Equal(30, resumedDefinition.Health);
    }

    static string Variant(Guid id, Guid baseId, int health) => $$"""
        { "Id": "{{id}}", "__Variant": { "Base": "{{baseId}}", "Overrides": { "Health": {{health}} } } }
        """;
}
