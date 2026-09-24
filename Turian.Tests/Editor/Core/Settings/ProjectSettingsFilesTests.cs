namespace Turian.Tests.Editor;

/// <summary>
/// Covers projects as plain folders whose settings are data assets: an older <c>project.data</c> is
/// converted and set aside, settings are found by their class with nothing listing them, duplicates and
/// missing folders are reported, and a build's index names the asset each kind uses.
/// </summary>
public class ProjectSettingsFilesTests : IDisposable
{
    static readonly Guid sceneId = Guid.Parse("13bd3bf4-dbe0-47cf-a32d-fc29736b2a0f");
    static readonly Guid actionsId = Guid.Parse("b1000003-0000-4000-8000-000000000100");

    readonly string root = Path.Combine(Path.GetTempPath(), $"TurianSettings_{Guid.NewGuid():N}");
    readonly string legacyPath;

    /// <summary>Creates an empty project folder.</summary>
    public ProjectSettingsFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        legacyPath = Path.Combine(root, SettingsService.LegacyProjectFileName);
    }

    /// <summary>Removes the project folder.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    void WriteLegacyProject() => File.WriteAllText(legacyPath, $$"""
        {
          "__TypeId": "a3000000-0000-4000-8000-000000000009",
          "Title": "Legacy Game",
          "CompanyName": "Studio",
          "ApplicationIdentifier": "com.studio.legacy",
          "Version": "1.2.3",
          "StartupScene": { "AssetId": "{{sceneId}}" },
          "TextureMaxResolution": 1024,
          "Id": "accf45ba-7e65-4fd7-82a9-c9cb75fc4736",
          "InputActions": { "AssetId": "{{actionsId}}" }
        }
        """);

    AppSettings Open()
    {
        var project = SettingsService.Load(root);
        Assert.NotNull(project);
        ProjectSettingsLoader.LoadFromSources(project);
        return project;
    }

    /// <summary>The values an older project carried inline end up in its settings assets.</summary>
    [Fact]
    public void ALegacyProjectMovesItsValuesIntoSettingsAssets()
    {
        WriteLegacyProject();

        Assert.True(ProjectSettingsFiles.MigrateLegacyProject(root));

        var project = Open();
        var player = project.Get<PlayerSettings>();

        Assert.Equal("Legacy Game", player.ProductName);
        Assert.Equal("Studio", player.Author);
        Assert.Equal("com.studio.legacy", player.ApplicationIdentifier);
        Assert.Equal("1.2.3", player.Version);
        Assert.Equal(sceneId, player.StartupScene?.AssetId);
        Assert.Equal(actionsId, project.Get<InputSettings>().Actions?.AssetId);
        Assert.Equal(1024, project.Get<GraphicsSettings>().TextureMaxResolution);
    }

    /// <summary>The converted project has no project file left; the old one is kept aside.</summary>
    [Fact]
    public void AMigratedProjectIsJustItsFolder()
    {
        WriteLegacyProject();
        ProjectSettingsFiles.MigrateLegacyProject(root);

        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists($"{legacyPath}.bak"));
        Assert.False(ProjectSettingsFiles.MigrateLegacyProject(root));
        Assert.Equal(Path.GetFileName(root), Open().Title);
    }

    /// <summary>A kind the project has no asset for reads as its declared defaults, the same instance each time.</summary>
    [Fact]
    public void AMissingKindReadsAsItsDefaults()
    {
        var project = Open();

        Assert.Equal(0, project.Get<GraphicsSettings>().TextureMaxResolution);
        Assert.Same(project.Get<GraphicsSettings>(), project.Get<GraphicsSettings>());
    }

    /// <summary>A settings asset anywhere under Assets is found by its class; nothing has to list it.</summary>
    [Fact]
    public void SettingsAreFoundByTheirClassWherever()
    {
        var project = Open();
        var elsewhere = Path.Combine(root, "Assets", "Config");
        Directory.CreateDirectory(elsewhere);
        var path = ProjectSettingsFiles.Create(project, new GraphicsSettings { TextureMaxResolution = 512 });
        File.Move(path, Path.Combine(elsewhere, "Graphics.dataasset"));
        File.Move($"{path}.meta", Path.Combine(elsewhere, "Graphics.dataasset.meta"));

        Assert.Equal(512, Open().Get<GraphicsSettings>().TextureMaxResolution);
    }

    /// <summary>Asking for a missing kind creates it once; asking again finds the same asset.</summary>
    [Fact]
    public void EnsuringAKindCreatesItOnce()
    {
        var project = Open();

        var created = ProjectSettingsFiles.Ensure(project, typeof(PlayerSettings));
        var found = ProjectSettingsFiles.Ensure(project, typeof(PlayerSettings));

        Assert.Equal(created, found);
        Assert.Single(ProjectSettingsLoader.FindSources(project.AssetsAbsoluteDir));
    }

    /// <summary>Two assets of one kind are reported, and the one under Assets/Settings is the one used.</summary>
    [Fact]
    public void ADuplicatedKindIsReportedAndTheSettingsFolderWins()
    {
        var project = Open();
        var other = Path.Combine(root, "Assets", "Other");
        Directory.CreateDirectory(other);
        var stray = ProjectSettingsFiles.Create(project, new GraphicsSettings { TextureMaxResolution = 256 });
        File.Move(stray, Path.Combine(other, "A.dataasset"));
        File.Move($"{stray}.meta", Path.Combine(other, "A.dataasset.meta"));
        ProjectSettingsFiles.Create(project, new GraphicsSettings { TextureMaxResolution = 2048 });

        var issue = Assert.Single(ProjectValidator.Validate(root));

        Assert.Equal(ProjectIssueSeverity.Warning, issue.Severity);
        Assert.Equal(2048, Open().Get<GraphicsSettings>().TextureMaxResolution);
    }

    /// <summary>A folder without Assets is not a project.</summary>
    [Fact]
    public void AFolderWithoutAssetsIsNotAProject()
    {
        Directory.Delete(Path.Combine(root, "Assets"));

        Assert.Null(SettingsService.Load(root));
        Assert.Equal(ProjectIssueSeverity.Error, Assert.Single(ProjectValidator.Validate(root)).Severity);
    }

    /// <summary>A build's index names the asset each kind uses, by id.</summary>
    [Fact]
    public void TheBuildIndexListsTheAssetEachKindUses()
    {
        var project = Open();
        ProjectSettingsFiles.Create(project, new PlayerSettings());
        ProjectSettingsFiles.Create(project, new GraphicsSettings());
        var index = Path.Combine(root, "out", ProjectSettingsLoader.IndexFileName);

        ProjectSettingsLoader.WriteIndex(project.AssetsAbsoluteDir, index);

        using var document = JsonDocument.Parse(File.ReadAllText(index));
        var ids = document.RootElement.GetProperty("Settings").EnumerateArray().Select(id => id.GetGuid());
        Assert.Equal(
            ProjectSettingsLoader.FindSources(project.AssetsAbsoluteDir).Select(source => source.AssetId).Order(),
            ids.Order());
    }
}
