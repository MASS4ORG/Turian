namespace Turian.Tests;

/// <summary>The project's own import settings for assets that live in bricks.</summary>
public sealed class PackageImportOverridesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-import-overrides-{Guid.NewGuid():N}");
    readonly Guid iconId = Guid.NewGuid();

    /// <summary>Creates a project that installs a local brick holding one texture.</summary>
    public PackageImportOverridesTests()
    {
        var brick = Path.Combine(root, "icons");
        Directory.CreateDirectory(Path.Combine(brick, "Runtime"));
        new PackageManifest { Name = "user.mateo.icons", Version = SemanticVersion.Parse("1.0.0") }.Save(brick);
        using (var bitmap = new SKBitmap(4, 4))
        using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(brick, "Runtime", "Icon.png"), png.ToArray());
        File.WriteAllText(Path.Combine(brick, "Runtime", "Icon.png.meta"), $$"""
            { "__TypeId": "f12b8bbf-74b4-5af6-95a3-535c4fa6c16c", "IsSrgb": true, "GenerateMips": true,
              "ImportSettings": { "MaxResolution": 0, "PerTarget": {} }, "RelativePath": "Runtime/Icon.png", "Id": "{{iconId}}" }
            """);

        Directory.CreateDirectory(Path.Combine(root, "game", "Assets"));
        ProjectBricks.Add(Path.Combine(root, "game"), "user.mateo.icons", "file:../../icons");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    /// <summary>Overrides are stored per asset and property, replace only what they name, and cannot change the asset's identity.</summary>
    [Fact]
    public void OverridesAreStoredAndLaidOverTheMeta()
    {
        var project = Path.Combine(root, "game");
        var overrides = new PackageImportOverrides();
        overrides.Set(iconId, "GenerateMips", false);
        overrides.Set(iconId, "ImportSettings", new JsonObject { ["MaxResolution"] = 128 });
        Assert.Throws<ArgumentException>(() => overrides.Set(iconId, "Id", Guid.NewGuid().ToString()));
        overrides.Save(project);

        var loaded = PackageImportOverrides.Load(project);
        var merged = JsonNode.Parse(loaded.Apply(iconId, """{ "Id": "x", "IsSrgb": true, "GenerateMips": true, "ImportSettings": { "MaxResolution": 0 } }"""))!;

        Assert.False((bool)merged["GenerateMips"]!);
        Assert.True((bool)merged["IsSrgb"]!);
        Assert.Equal(128, (int)merged["ImportSettings"]!["MaxResolution"]!);
        Assert.Equal("x", (string)merged["Id"]!);

        loaded.Set(iconId, "GenerateMips", null);
        loaded.Set(iconId, "ImportSettings", null);
        loaded.Save(project);
        Assert.False(File.Exists(Path.Combine(project, PackageImportOverrides.RelativePath)));
    }

    /// <summary>An override changes how the brick's asset imports, without touching the brick.</summary>
    [Fact]
    public void OverridesChangeTheImport()
    {
        var project = Path.Combine(root, "game");
        var metaPath = Path.Combine(root, "icons", "Runtime", "Icon.png.meta");
        var before = File.ReadAllText(metaPath);

        var plain = SettingsHash(project);
        var overrides = new PackageImportOverrides();
        overrides.Set(iconId, "GenerateMips", false);
        overrides.Save(project);
        var changed = SettingsHash(project);

        Assert.NotEqual(plain, changed);
        Assert.Equal(before, File.ReadAllText(metaPath));
    }

    string SettingsHash(string project)
    {
        if (Directory.Exists(Path.Combine(project, ".Cache"))) Directory.Delete(Path.Combine(project, ".Cache"), recursive: true);
        ProjectPackages.Invalidate(project);
        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        var manifest = Path.Combine(project, ".Cache", "Assets", "by-guid", iconId.ToString("N"), "import.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        return document.RootElement.GetProperty("SettingsHash").GetString()!;
    }
}
