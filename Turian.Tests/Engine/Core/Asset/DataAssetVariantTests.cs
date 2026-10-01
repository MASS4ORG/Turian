namespace Turian.Tests;

/// <summary>Data asset variants: a base plus overrides, resolved wherever a payload is read.</summary>
public sealed class DataAssetVariantTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-variants-{Guid.NewGuid():N}");
    readonly Guid baseId = Guid.NewGuid();
    readonly Guid variantId = Guid.NewGuid();
    const string baseJson = """{ "__TypeId": "ca028d68-85a9-5f3d-ad2f-c54db757d6fa", "Name": "Base", "Stats": { "Health": 10, "Mana": 5 }, "Tags": ["a", "b"] }""";

    /// <summary>Creates the scratch folder.</summary>
    public DataAssetVariantTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Overrides merge into objects, replace lists and scalars, and null removes; the variant keeps its own id.</summary>
    [Fact]
    public void OverridesLayerOverTheBase()
    {
        var variant = $$"""
            { "Id": "{{variantId}}", "__Variant": { "Base": "{{baseId}}",
              "Overrides": { "Name": "Boss", "Stats": { "Health": 99 }, "Tags": ["z"], "Mana": null } } }
            """;

        var flat = JsonNode.Parse(DataAssetVariants.Flatten(variant, id => id == baseId ? baseJson : null))!;

        Assert.Equal("Boss", (string)flat["Name"]!);
        Assert.Equal(99, (int)flat["Stats"]!["Health"]!);
        Assert.Equal(5, (int)flat["Stats"]!["Mana"]!);
        Assert.Equal(["z"], flat["Tags"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(variantId.ToString(), (string)flat["Id"]!);
        Assert.Equal("ca028d68-85a9-5f3d-ad2f-c54db757d6fa", (string)flat["__TypeId"]!);
        Assert.Null(flat["__Variant"]);
    }

    /// <summary>Files that are not variants pass through untouched, and variants of variants resolve down the chain.</summary>
    [Fact]
    public void ChainsResolveAndPlainFilesPassThrough()
    {
        Assert.Equal(baseJson, DataAssetVariants.Flatten(baseJson, _ => throw new InvalidOperationException()));

        var middle = Guid.NewGuid();
        var middleJson = $$"""{ "__Variant": { "Base": "{{baseId}}", "Overrides": { "Name": "Middle", "Stats": { "Mana": 50 } } } }""";
        var top = $$"""{ "__Variant": { "Base": "{{middle}}", "Overrides": { "Stats": { "Health": 1 } } } }""";

        var flat = JsonNode.Parse(DataAssetVariants.Flatten(top, id => id == baseId ? baseJson : id == middle ? middleJson : null))!;

        Assert.Equal("Middle", (string)flat["Name"]!);
        Assert.Equal(1, (int)flat["Stats"]!["Health"]!);
        Assert.Equal(50, (int)flat["Stats"]!["Mana"]!);
    }

    /// <summary>A missing base and a cycle are reported instead of looping or returning half a payload.</summary>
    [Fact]
    public void BrokenChainsFail()
    {
        var variant = $$"""{ "__Variant": { "Base": "{{baseId}}" } }""";
        Assert.Throws<InvalidOperationException>(() => DataAssetVariants.Flatten(variant, _ => null));

        var selfReferencing = $$"""{ "__Variant": { "Base": "{{variantId}}" } }""";
        Assert.Throws<InvalidOperationException>(() => DataAssetVariants.Flatten(selfReferencing, _ => selfReferencing));
    }

    /// <summary>The factory writes a variant of a real file with its own meta, and the file resolves against the base.</summary>
    [Fact]
    public void FactoryWritesALoadableVariant()
    {
        var baseFile = Path.Combine(root, "Assets", "Hero.dataasset");
        Directory.CreateDirectory(Path.GetDirectoryName(baseFile)!);
        File.WriteAllText(baseFile, baseJson);
        File.WriteAllText($"{baseFile}.meta", $$"""{ "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c", "RelativePath": "Assets/Hero.dataasset", "Id": "{{baseId}}" }""");

        var (path, id) = DataAssetVariantFactory.Create(root, baseFile, "Assets/Variants", "Boss");

        Assert.Equal(Path.Combine(root, "Assets", "Variants", "Boss.dataasset"), path);
        Assert.True(DataAssetVariants.IsVariant(File.ReadAllText(path)));
        Assert.Contains(id.ToString(), File.ReadAllText($"{path}.meta"), StringComparison.Ordinal);
        Assert.Contains("ca028d68-85a9-5f3d-ad2f-c54db757d6fa", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => DataAssetVariantFactory.Create(root, baseFile, "Assets/Variants", "Boss"));
        Assert.Null(new GenericAssetImporter().LoadAuthoredContent(new DataAssetAsset(), path));
    }

    /// <summary>
    /// An imported project loads the variant as the base's own type with the overrides applied, in the editor where the
    /// source exists and from the imported copy alone, as a built game does.
    /// </summary>
    [Fact]
    public void ImportedVariantsLoadAsTheirBaseType()
    {
        TestAssetDatabase.Reset();
        try
        {
            var project = Path.Combine(root, "game");
            var assets = Path.Combine(project, "Assets");
            Directory.CreateDirectory(assets);
            var baseFile = Path.Combine(assets, "Base.dataasset");
            File.WriteAllText(baseFile, $$"""{ "__TypeId": "{{AssemblyDefinition.TypeIdValue}}", "Name": "Base.Asm", "RootNamespace": "Base", "Id": "{{baseId}}" }""");
            File.WriteAllText($"{baseFile}.meta", $$"""{ "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c", "RelativePath": "Assets/Base.dataasset", "Id": "{{baseId}}" }""");
            var (variantFile, id) = DataAssetVariantFactory.Create(project, baseFile, "Assets", "Child");
            File.WriteAllText(variantFile, File.ReadAllText(variantFile).Replace("\"Overrides\": {}", "\"Overrides\": { \"Name\": \"Child.Asm\" }", StringComparison.Ordinal));

            var settings = new SettingsService();
            settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
            using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
            var database = new AssetDatabase();
            using var importer = new AssetImporter(NullLogger.Instance, database, settings);
            importer.GenerateMetaFiles(assets);

            var meta = Assert.IsType<DataAssetAsset>(Asset.Load($"{variantFile}.meta"));
            var fromSource = Assert.IsType<AssemblyDefinition>(meta.GetContent(project));
            Assert.Equal("Child.Asm", fromSource.Name);
            Assert.Equal("Base", fromSource.RootNamespace);
            Assert.Equal(id, fromSource.Id);

            // The runtime reads imported assets without an editor watcher removing their cache entries.
            importer.Dispose();
            File.Delete(variantFile);
            var fromImport = Assert.IsType<AssemblyDefinition>(Assert.IsType<DataAssetAsset>(Asset.Load($"{variantFile}.meta")).Reload(project));
            Assert.Equal("Child.Asm", fromImport.Name);
            Assert.True(database.TryGetAsset(id, out var record));
            Assert.Equal(Guid.Parse(AssemblyDefinition.TypeIdValue), record!.DataAssetPayloadTypeId);
        }
        finally
        {
            TestAssetDatabase.Reset();
        }
    }
}
