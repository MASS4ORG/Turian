using Gaya.Packages;

namespace Turian.Tests;

/// <summary>
/// The project asset: the settings and build profiles it registers, a profile's overrides through variants,
/// migrating a project that has none, and the engine version it records.
/// </summary>
public sealed class ProjectAssetTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-project-asset-").FullName;

    /// <summary>Creates an empty project.</summary>
    public ProjectAssetTests() => Directory.CreateDirectory(Assets);

    string Assets => Path.Combine(project, "Assets");

    AppSettings Settings => new() { Title = "Game", ProjectAbsoluteDir = project };

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    Guid Save(string relativePath, DataAsset payload)
    {
        var id = Guid.NewGuid();
        payload.Id = id;
        var path = Path.Combine(Assets, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Serializer.Save(path, payload);
        SaveMeta(path, id);
        return id;
    }

    Guid SaveVariant(string relativePath, Guid baseId, string baseRelativePath, string overridesJson)
    {
        var id = Guid.NewGuid();
        var baseJson = File.ReadAllText(Path.Combine(Assets, baseRelativePath));
        var root = JsonNode.Parse(DataAssetVariants.Create(baseId, baseJson, id))!;
        root[DataAssetVariants.Property]!["Overrides"] = JsonNode.Parse(overridesJson);
        var path = Path.Combine(Assets, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString());
        SaveMeta(path, id);
        return id;
    }

    void SaveMeta(string path, Guid id) => Serializer.Save($"{path}.meta",
        new DataAssetAsset { Id = id, RelativePath = Path.GetRelativePath(project, path) });

    BuildAppSettings BuildSettings => new() { ProjectAbsoluteDir = project };

    /// <summary>
    /// Only registered settings count: a second asset of the kind elsewhere in the project is ignored.
    /// </summary>
    [Fact]
    public void OnlyRegisteredSettingsAreInForce()
    {
        var registered = Save("Config/Player.dataasset", new PlayerSettings { ProductName = "Registered" });
        Save("Settings/PlayerSettings.dataasset", new PlayerSettings { ProductName = "Stray" });
        ProjectSettingsFiles.CreateProjectAsset(Settings, [registered], []);

        var settings = Settings;
        ProjectSettingsLoader.LoadFromSources(settings);

        Assert.Equal(registered, Assert.Single(ProjectSettingsLoader.FindSources(Assets)).AssetId);
        Assert.Equal("Registered", settings.Get<PlayerSettings>().ProductName);
    }

    /// <summary>
    /// A build profile's variant replaces the project's settings of its kind: overridden values change, the rest
    /// comes from the base, and the index a build writes lists the variant.
    /// </summary>
    [Fact]
    public void ProfileVariantsOverrideTheProjectSettings()
    {
        var scene = new AssetReference<Prefab>(Guid.NewGuid());
        var player = Save("Settings/PlayerSettings.dataasset",
            new PlayerSettings { ProductName = "Game", StartupScene = scene });
        var input = Save("Settings/InputSettings.dataasset", new InputSettings());
        var server = SaveVariant("Settings/Build/Server Player.dataasset", player, "Settings/PlayerSettings.dataasset",
            """{ "ProductName": "Game Server" }""");
        ProjectSettingsFiles.CreateProjectAsset(Settings, [player, input], []);
        var profile = new BuildProfile { Target = BuildTarget.Server, Settings = [new(server)] };

        var settings = Settings;
        ProjectSettingsLoader.LoadFromSources(settings, profile);
        var index = Path.Combine(project, "out", ProjectSettingsLoader.IndexFileName);
        ProjectSettingsLoader.WriteIndex(Assets, index, profile);

        Assert.Equal("Game Server", settings.Get<PlayerSettings>().ProductName);
        Assert.Equal(scene.AssetId, settings.Get<PlayerSettings>().StartupScene!.AssetId);
        var written = JsonDocument.Parse(File.ReadAllText(index)).RootElement.GetProperty("Settings")
            .EnumerateArray().Select(static id => id.GetGuid());
        Assert.Equal(new[] { input, server }.Order(), written.Order());
    }

    /// <summary>
    /// Migrating registers what a search found, a kind's preferred copy and every build profile; it happens once,
    /// and duplicates stay unregistered.
    /// </summary>
    [Fact]
    public void MigrationRegistersWhatTheSearchFound()
    {
        var preferred = Save("Settings/PlayerSettings.dataasset", new PlayerSettings { ProductName = "Preferred" });
        Save("Other/PlayerSettings.dataasset", new PlayerSettings { ProductName = "Duplicate" });
        var profile = Save("Settings/Build/Server.dataasset", new BuildProfile { Target = BuildTarget.Server });

        Assert.True(ProjectSettingsFiles.MigrateToProjectAsset(project));
        Assert.False(ProjectSettingsFiles.MigrateToProjectAsset(project));

        var asset = ProjectAsset.Load(Assets)!;
        Assert.Equal([preferred], asset.Settings.Select(static r => r.AssetId));
        Assert.Equal([profile], asset.BuildProfiles.Select(static r => r.AssetId));
        Assert.Equal(ProjectSettingsFiles.EngineVersionStamp, asset.EngineVersion);
        Assert.Equal(Asset.Load($"{ProjectAsset.PathIn(Assets)}.meta")!.Id, asset.Id);
        Assert.Equal("Server", Assert.Single(BuildProfiles.Discover(BuildSettings)).Name);
    }

    /// <summary>
    /// A created build profile always registers; a created settings asset only when its kind has none, and nothing
    /// registers in a project without a project asset.
    /// </summary>
    [Fact]
    public void CreatedAssetsRegisterOnceAKindIsFree()
    {
        Assert.False(ProjectSettingsFiles.RegisterCreated(Settings, new BuildProfile(), Guid.NewGuid()));
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);

        var first = Save("Settings/PlayerSettings.dataasset", new PlayerSettings());
        Assert.True(ProjectSettingsFiles.RegisterCreated(Settings, new PlayerSettings(), first));
        Assert.False(ProjectSettingsFiles.RegisterCreated(Settings, new PlayerSettings(), Guid.NewGuid()));
        Assert.False(ProjectSettingsFiles.RegisterCreated(Settings, new InputActionsAsset(), Guid.NewGuid()));
        var profilePath =
            ProjectSettingsFiles.CreateBuildProfile(Settings, new BuildProfile { Name = "Client" }, active: true);

        var asset = ProjectAsset.Load(Assets)!;
        Assert.Equal([first], asset.Settings.Select(static r => r.AssetId));
        Assert.Equal(Path.Combine(Assets, "Settings", "Build", "Client.dataasset"), profilePath);
        Assert.Equal("Client", BuildProfiles.Active(BuildSettings)!.Name);
    }

    /// <summary>
    /// Opening stamps a project the engine has not saved or that an older engine saved; a project already at this
    /// version, or without a project asset, is left as it is.
    /// </summary>
    [Fact]
    public void OpeningRecordsTheEngineVersion()
    {
        Assert.Null(ProjectSettingsFiles.RecordEngineVersion(project));
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);
        var asset = ProjectAsset.Load(Assets)!;
        asset.EngineVersion = "0.1.0";
        Serializer.Save<DataAsset>(ProjectAsset.PathIn(Assets), asset);

        Assert.Equal("0.1.0", ProjectSettingsFiles.RecordEngineVersion(project));
        Assert.Equal(ProjectSettingsFiles.EngineVersionStamp, ProjectAsset.Load(Assets)!.EngineVersion);
        Assert.Null(ProjectSettingsFiles.RecordEngineVersion(project));
    }

    /// <summary>
    /// The validator refuses a project saved by a newer major engine, warns about a newer minor or an older one,
    /// and about registered settings whose file is gone.
    /// </summary>
    [Theory]
    [InlineData(1, 0, ProjectIssueSeverity.Error)]
    [InlineData(0, 1, ProjectIssueSeverity.Warning)]
    [InlineData(-1, 0, ProjectIssueSeverity.Warning)]
    public void ValidationComparesTheEngineVersion(int majorDelta, int minorDelta, ProjectIssueSeverity expected)
    {
        var current = ProjectPackages.EngineVersion;
        var saved = new SemanticVersion(current.Major + majorDelta, current.Minor + minorDelta, 0);
        ProjectSettingsFiles.CreateProjectAsset(Settings, [Guid.NewGuid()], []);
        var asset = ProjectAsset.Load(Assets)!;
        asset.EngineVersion = saved.ToString();
        Serializer.Save<DataAsset>(ProjectAsset.PathIn(Assets), asset);

        var issues = ProjectValidator.Validate(project);

        Assert.Equal(expected, issues[0].Severity);
        Assert.Contains("Turian", issues[0].Message);
        Assert.Contains(issues, static issue => issue.Message.Contains("has no file"));
    }

    /// <summary>
    /// A project asset that cannot be read is treated as missing, so the project falls back to searching.
    /// </summary>
    [Fact]
    public void UnreadableProjectAssetsFallBackToSearching()
    {
        var player = Save("Settings/PlayerSettings.dataasset", new PlayerSettings());
        File.WriteAllText(ProjectAsset.PathIn(Assets), "{ not json");

        Assert.Null(ProjectAsset.Load(Assets));
        Assert.Equal(player, Assert.Single(ProjectSettingsLoader.FindSources(Assets)).AssetId);
    }

    /// <summary>
    /// The source index reads the editor's catalog and falls back to meta files for ids it does not know.
    /// </summary>
    [Fact]
    public void SourceIndexUsesTheCatalogThenMetaFiles()
    {
        var player = Save("Settings/PlayerSettings.dataasset", new PlayerSettings { ProductName = "Indexed" });
        var database = new AssetDatabase();
        database.BuildDatabase(Assets);
        database.SaveCatalog(project);
        var late = Save("Settings/InputSettings.dataasset", new InputSettings());

        var index = new AssetSourceIndex(project);

        Assert.Equal(Path.Combine(Assets, "Settings", "PlayerSettings.dataasset"), index.PathOf(player));
        Assert.Equal(Path.Combine(Assets, "Settings", "InputSettings.dataasset"), index.PathOf(late));
        Assert.Null(index.PathOf(Guid.NewGuid()));
        Assert.Equal("Indexed", Assert.IsType<PlayerSettings>(index.Load(player)).ProductName);
        Assert.Null(index.Load(Guid.NewGuid()));
    }
}
