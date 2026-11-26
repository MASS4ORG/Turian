namespace Turian.Tests;

/// <summary>A meta whose asset type comes from an assembly that is not loaded must survive a scan untouched.</summary>
public sealed class MissingTypeMetaTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-missing-type-{Guid.NewGuid():N}");

    /// <summary>Creates an empty project.</summary>
    public MissingTypeMetaTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));

        // Scanning metas does not depend on the renderer brick, whose packages every scan would otherwise restore.
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(project, recursive: true);
    }

    /// <summary>Scanning keeps the meta, so the asset keeps its id once the assembly that provides its type is back.</summary>
    [Fact]
    public void ScanLeavesMetasOfUnavailableTypes()
    {
        var asset = Path.Combine(project, "Assets", "theme.uss");
        var meta = $"{asset}.meta";
        File.WriteAllText(asset, "a {}");
        const string original = """{ "__TypeId": "0f0f0f0f-0000-4000-8000-000000000001", "Id": "bee4af23-f013-4894-941e-01ac398d4d9a" }""";
        File.WriteAllText(meta, original);

        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
        Assert.Equal(original, File.ReadAllText(meta));

        // What the folder watcher runs when the file is edited.
        typeof(AssetImporter).GetMethod("EnsureAssetImported", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(importer, [asset, true]);
        Assert.Equal(original, File.ReadAllText(meta));
    }

    /// <summary>A type that fails to load keeps its meta and id exactly like an unknown one.</summary>
    [Fact]
    public void ScanLeavesMetasOfTypesThatFailToInitialize()
    {
        var typeId = Guid.NewGuid();
        TypeRegistry.Register(typeId, typeof(FailingInitializerAsset));
        var asset = Path.Combine(project, "Assets", "broken.txt");
        var meta = $"{asset}.meta";
        File.WriteAllText(asset, "a");
        var original = $$"""{ "__TypeId": "{{typeId}}", "Id": "bee4af23-f013-4894-941e-01ac398d4d9b" }""";
        File.WriteAllText(meta, original);

        using var importer = Importer();
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
        Invoke(importer, "EnsureAssetImported", asset, true);

        Assert.Equal(original, File.ReadAllText(meta));
    }

    /// <summary>Only a meta that cannot be parsed is replaced.</summary>
    [Fact]
    public void ScanReplacesMalformedMetas()
    {
        var asset = Path.Combine(project, "Assets", "garbled.txt");
        var meta = $"{asset}.meta";
        File.WriteAllText(asset, "a");
        File.WriteAllText(meta, "{ not json");

        using var importer = Importer();
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        Assert.NotNull(Asset.Load(meta));
    }

    /// <summary>The type check looks through the whole exception chain and ignores plain parse and file failures.</summary>
    [Fact]
    public void UnavailableTypesAreRecognizedInTheChain()
    {
        var missing = new FileNotFoundException("missing", "Some.Dependency, Version=1.0.0.0");
        Assert.True(IsUnavailable(new JsonException("wrapped", new TypeInitializationException("<Module>", missing))));
        Assert.True(IsUnavailable(new InvalidOperationException("outer", new UnresolvableTypeIdException(Guid.NewGuid(), "unknown"))));
        Assert.True(IsUnavailable(new AggregateException(new TypeLoadException("gone")).InnerException!));
        Assert.False(IsUnavailable(new JsonException("malformed")));
        Assert.False(IsUnavailable(new FileNotFoundException("gone", Path.Combine(project, "Assets", "a.txt"))));
    }

    /// <summary>
    /// An asset skipped because its type was not registered is imported once the type is, with no restart: a
    /// compile that swaps user code raises the same signal.
    /// </summary>
    [Fact]
    public void SkippedAssetsImportOnceTheirTypeIsRegistered()
    {
        var typeId = Guid.NewGuid();
        var asset = Path.Combine(project, "Assets", "later.txt");
        var meta = $"{asset}.meta";
        File.WriteAllText(asset, "a");
        var id = Guid.NewGuid();
        var original = $$"""{ "__TypeId": "{{typeId}}", "Id": "{{id}}" }""";
        File.WriteAllText(meta, original);

        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        var database = new AssetDatabase();
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, database, settings, build);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
        Assert.False(database.Assets.ContainsKey(id));
        Assert.Equal(0, importer.ImportDeferredAssets());

        TypeRegistry.Register(typeId, typeof(DeferredAsset));
        var raised = new List<Guid>();
        database.AssetReimported += raised.Add;
        (typeof(BuildManager).GetField(nameof(BuildManager.UserAssembliesChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(build) is Action raise ? raise : throw new InvalidOperationException()).Invoke();

        Assert.True(database.Assets.ContainsKey(id));
        Assert.Contains(id, raised);
        Assert.Equal(original, File.ReadAllText(meta));
        Assert.Equal(0, importer.ImportDeferredAssets());
    }

    AssetImporter Importer()
    {
        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        return new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings);
    }

    static void Invoke(AssetImporter importer, string method, params object[] arguments) =>
        typeof(AssetImporter).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(importer, arguments);

    static bool IsUnavailable(Exception exception) =>
        (bool)typeof(AssetImporter).GetMethod("IsUnavailableType", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [exception])!;

    sealed class DeferredAsset : Asset;

    sealed class FailingInitializerAsset : Asset
    {
        static FailingInitializerAsset() =>
            throw new FileNotFoundException("missing", "Some.Dependency, Version=1.0.0.0");
    }
}
