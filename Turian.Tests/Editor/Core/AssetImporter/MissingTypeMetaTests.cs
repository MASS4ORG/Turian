namespace Turian.Tests;

/// <summary>A meta whose asset type comes from an assembly that is not loaded must survive a scan untouched.</summary>
[Collection(SerialTests.Name)]
public sealed class MissingTypeMetaTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-missing-type-{Guid.NewGuid():N}");

    /// <summary>Creates an empty project.</summary>
    public MissingTypeMetaTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        TestAssetDatabase.Reset();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
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

    /// <summary>An importer built before the project opened still finds the importers of the project's bricks.</summary>
    [Fact]
    public void ImporterBuiltEarlyGainsBrickImporters()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var settings = new SettingsService();
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings);

        new ProjectManifest { Dependencies = { ["org.mass4.turian.ui"] = "builtin:org.mass4.turian.ui" } }
            .Save(project);
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        File.WriteAllText(Path.Combine(project, "Assets", "hud.ui"), "<ui/>");
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        Assert.IsType<UiDocumentAssetImporter>(importer.ImporterFor(Path.Combine(project, "Assets", "hud.ui")));
    }
}
