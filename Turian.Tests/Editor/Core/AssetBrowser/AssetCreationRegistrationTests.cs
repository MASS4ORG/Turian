namespace Turian.Tests;

/// <summary>Settings and build profiles created from the asset browser register in the project asset.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetCreationRegistrationTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-asset-creation-").FullName;

    string Assets => Path.Combine(project, "Assets");

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>
    /// The first settings asset of a kind and every build profile register; a second asset of the kind does not.
    /// </summary>
    [Fact]
    public void CreatedSettingsAndProfilesRegister()
    {
        Directory.CreateDirectory(Assets);
        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = project };
        settings.Set(app);
        ProjectSettingsFiles.CreateProjectAsset(app, [], []);
        var logger = Substitute.For<ILogger>();
        using var build = new BuildManager(app, logger);
        using var importer = new AssetImporter(logger, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
        var catalog = new AssetCreationCatalog(new AssetFileSystem(settings, importer), settings, build, logger);
        var player = new AssetCreationKind("Settings/Player", "PlayerSettings.dataasset", typeof(PlayerSettings));

        Assert.True(catalog.Create(player, Assets));
        Assert.True(catalog.Create(player, Assets));
        Assert.True(catalog.Create(new AssetCreationKind("Build/Profile", "Server.dataasset", typeof(BuildProfile)), Assets));

        var asset = ProjectAsset.Load(Assets)!;
        var registered = Assert.Single(asset.Settings).AssetId;
        Assert.Equal(registered, Asset.Load(Path.Combine(Assets, "PlayerSettings.dataasset.meta"))!.Id);
        Assert.Single(asset.BuildProfiles);
    }

    /// <summary>A created data asset's payload id is its asset id, as the meta file records it.</summary>
    [Fact]
    public void CreatedPayloadIdMatchesMetaId()
    {
        Directory.CreateDirectory(Assets);
        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = project };
        settings.Set(app);
        var logger = Substitute.For<ILogger>();
        using var build = new BuildManager(app, logger);
        using var importer = new AssetImporter(logger, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
        var catalog = new AssetCreationCatalog(new AssetFileSystem(settings, importer), settings, build, logger);
        var path = Path.Combine(Assets, "Server.dataasset");

        Assert.True(catalog.Create(new AssetCreationKind("Build/Profile", "Server.dataasset", typeof(BuildProfile)),
            Assets));
        var created = ProjectSettingsFiles.Create(app, new InputSettings());

        Assert.Equal(Asset.Load($"{path}.meta")!.Id, Serializer.Load<DataAsset>(path)!.Id);
        Assert.Equal(Asset.Load($"{created}.meta")!.Id, Serializer.Load<DataAsset>(created)!.Id);
    }
}
