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
    /// The listing follows the project asset's order and marks the active profile; switching moves the mark, an
    /// unregistered id is refused, none clears it, and a registered profile whose file is gone is left out.
    /// </summary>
    [Fact]
    public void SwitchingProfilesMovesTheActiveMark()
    {
        Assert.Empty(BuildProfiles.List(Settings));
        Assert.False(ProjectSettingsFiles.SetActiveBuildProfile(Settings, null));
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], [Guid.NewGuid()]);
        ProjectSettingsFiles.CreateBuildProfile(Settings, new BuildProfile { Name = "Client" }, active: true);
        ProjectSettingsFiles.CreateBuildProfile(Settings, new BuildProfile { Name = "Server" });

        var listed = BuildProfiles.List(Settings);
        Assert.Equal(["Client", "Server"], listed.Select(static p => p.Profile.Name));
        Assert.Equal([true, false], listed.Select(static p => p.IsActive));

        Assert.True(ProjectSettingsFiles.SetActiveBuildProfile(Settings, listed[1].Id));
        Assert.Equal("Server", BuildProfiles.Active(BuildSettings)!.Name);
        Assert.False(ProjectSettingsFiles.SetActiveBuildProfile(Settings, Guid.NewGuid()));
        Assert.Equal([false, true], BuildProfiles.List(Settings).Select(static p => p.IsActive));

        Assert.True(ProjectSettingsFiles.SetActiveBuildProfile(Settings, null));
        Assert.Null(BuildProfiles.Active(BuildSettings));
        Assert.DoesNotContain(BuildProfiles.List(Settings), static p => p.IsActive);
    }

    /// <summary>Added profiles are named after their target, numbered past names in use, and not made active.</summary>
    [Fact]
    public void AddedProfilesAreNamedAfterTheirTarget()
    {
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);

        ProjectSettingsFiles.AddBuildProfile(Settings, BuildTarget.Server);
        var second = ProjectSettingsFiles.AddBuildProfile(Settings, BuildTarget.Server);
        ProjectSettingsFiles.AddBuildProfile(Settings, BuildTarget.Client);

        var listed = BuildProfiles.List(Settings);
        Assert.Equal(["Server", "Server 1", "Client"], listed.Select(static p => p.Profile.Name));
        Assert.Equal([BuildTarget.Server, BuildTarget.Server, BuildTarget.Client],
            listed.Select(static p => p.Profile.Profile.Target));
        Assert.Equal(Path.Combine(Assets, "Settings", "Build", "Server 1.dataasset"), second);
        Assert.DoesNotContain(listed, static p => p.IsActive);
    }

    /// <summary>Non-desktop profiles start with the platform's supported target and runtime identifier.</summary>
    [Fact]
    public void AddedPlatformProfilesUseTheirOwnDefaults()
    {
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);
        var platform = Substitute.For<IBuildPlatform>();
        platform.Id.Returns("android");
        platform.DisplayName.Returns("Android");
        platform.Targets.Returns([BuildTarget.Client]);
        platform.RuntimeIdentifiers.Returns(["android-arm64", "android-x64"]);

        ProjectSettingsFiles.AddBuildProfile(Settings, platform, BuildTarget.Client);
        ProjectSettingsFiles.AddBuildProfile(Settings, platform, BuildTarget.Client);

        var profiles = BuildProfiles.List(Settings);
        Assert.Equal(["Android Client", "Android Client 1"], profiles.Select(static profile => profile.Profile.Name));
        Assert.All(profiles, static profile =>
        {
            Assert.Equal("android", profile.Profile.Profile.Platform);
            Assert.Equal("android-arm64", profile.Profile.Profile.RuntimeIdentifier);
            Assert.Equal(BuildTarget.Client, profile.Profile.Profile.Target);
        });
        Assert.Throws<ArgumentException>(() => ProjectSettingsFiles.AddBuildProfile(Settings, platform, BuildTarget.Server));
        Assert.Equal(2, BuildProfiles.List(Settings).Count);
    }

    /// <summary>Removing the active profile selects the next registered profile and leaves authored files available.</summary>
    [Fact]
    public void RemovingProfilesKeepsTheActiveSelectionValid()
    {
        Assert.False(ProjectSettingsFiles.RemoveBuildProfile(Settings, Guid.NewGuid()));
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);
        var first = ProjectSettingsFiles.CreateBuildProfile(Settings, new BuildProfile { Name = "Client" }, active: true);
        ProjectSettingsFiles.AddBuildProfile(Settings, BuildTarget.Server);
        var profiles = BuildProfiles.List(Settings);
        Assert.False(ProjectSettingsFiles.RemoveBuildProfile(Settings, Guid.NewGuid()));

        Assert.True(ProjectSettingsFiles.RemoveBuildProfile(Settings, profiles[0].Id));

        Assert.Equal(profiles[1].Id, Assert.Single(BuildProfiles.List(Settings)).Id);
        Assert.Equal("Server", BuildProfiles.Active(BuildSettings)!.Name);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(first + ".meta"));
        Assert.False(ProjectSettingsFiles.RemoveBuildProfile(Settings, profiles[0].Id));
        Assert.True(ProjectSettingsFiles.RemoveBuildProfile(Settings, profiles[1].Id));
        Assert.Empty(BuildProfiles.List(Settings));
        Assert.Null(BuildProfiles.Active(BuildSettings));
    }

    /// <summary>Removing an inactive profile preserves an explicitly active profile or the absence of one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAnInactiveProfilePreservesTheActiveChoice(bool active)
    {
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);
        ProjectSettingsFiles.CreateBuildProfile(Settings, new BuildProfile { Name = "Client" }, active);
        ProjectSettingsFiles.AddBuildProfile(Settings, BuildTarget.Server);
        var profiles = BuildProfiles.List(Settings);

        Assert.True(ProjectSettingsFiles.RemoveBuildProfile(Settings, profiles[1].Id));

        Guid? expected = active ? profiles[0].Id : null;
        Assert.Equal(expected, ProjectAsset.Load(Assets)!.ActiveBuildProfile?.AssetId);
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
    /// Deleting a registered profile or setting by hand can leave a <c>null</c> entry in the project asset; it is
    /// skipped rather than crashing the editor and the command line.
    /// </summary>
    [Fact]
    public void NullEntriesLeftByHandEditsAreSkipped()
    {
        var player = Save("Settings/PlayerSettings.dataasset", new PlayerSettings());
        var profile = Save("Settings/Build/Server.dataasset", new BuildProfile { Target = BuildTarget.Server });
        ProjectSettingsFiles.CreateProjectAsset(Settings, [player], [profile]);
        var path = ProjectAsset.PathIn(Assets);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["BuildProfiles"]!.AsArray().Insert(0, null);
        json["Settings"]!.AsArray().Insert(0, null);
        File.WriteAllText(path, json.ToJsonString());

        var asset = ProjectAsset.Load(Assets)!;
        Assert.Equal([profile], asset.BuildProfiles.Select(static r => r.AssetId));
        Assert.Equal([player], asset.Settings.Select(static r => r.AssetId));
        Assert.Equal("Server", Assert.Single(BuildProfiles.Discover(BuildSettings)).Name);
        Assert.Single(BuildProfiles.List(Settings));
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
