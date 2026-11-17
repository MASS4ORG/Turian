namespace Turian.Tests;

/// <summary>The Project Settings menu lists the settings the project's project asset registers, not a fixed set.</summary>
public sealed class ProjectSettingsMenuTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-settings-menu-").FullName;

    /// <summary>Creates an empty project.</summary>
    public ProjectSettingsMenuTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    AppSettings Settings
    {
        get
        {
            var settings = new AppSettings { Title = "Game", ProjectAbsoluteDir = project };
            if (!File.Exists(ProjectAsset.PathIn(settings.AssetsAbsoluteDir)))
            {
                ProjectSettingsFiles.CreateProjectAsset(settings, [], []);
            }

            return settings;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>Only the kinds the project registers appear, in the project asset's order.</summary>
    [Fact]
    public void ListsExactlyTheRegisteredKinds()
    {
        var settings = Settings;
        ProjectSettingsFiles.Create(settings, new TimeSettings());
        ProjectSettingsFiles.Create(settings, new PlayerSettings());

        var entries = ProjectSettingsMenu.Entries(settings);

        Assert.Equal([typeof(TimeSettings), typeof(PlayerSettings)], entries.Select(static e => e.Kind));
        Assert.Equal(["Time Settings", "Player Settings"], entries.Select(static e => e.Label));
    }

    /// <summary>A kind registered later appears, and a second asset of a registered kind adds no entry.</summary>
    [Fact]
    public void FollowsTheProjectAsset()
    {
        var settings = Settings;
        ProjectSettingsFiles.Create(settings, new PlayerSettings());
        Assert.Single(ProjectSettingsMenu.Entries(settings));

        ProjectSettingsFiles.Create(settings, new PlayerSettings());
        ProjectSettingsFiles.Create(settings, new GraphicsSettings());

        Assert.Equal([typeof(PlayerSettings), typeof(GraphicsSettings)],
            ProjectSettingsMenu.Entries(settings).Select(static e => e.Kind));
    }

    /// <summary>A project whose project asset registers nothing has no entries.</summary>
    [Fact]
    public void EmptyProjectHasNoEntries() => Assert.Empty(ProjectSettingsMenu.Entries(Settings));

    /// <summary>The label comes from the creation menu path, else from the type name.</summary>
    [Fact]
    public void LabelsFollowTheCreationPathOrTheTypeName()
    {
        Assert.Equal("Tags and Layers", ProjectSettingsMenu.Label(typeof(TagsAndLayersSettings)));
        Assert.Equal("Undecorated Project Settings", ProjectSettingsMenu.Label(typeof(UndecoratedProjectSettings)));
    }

    /// <summary>The stamp changes when the project asset is rewritten.</summary>
    [Fact]
    public void StampChangesWhenTheProjectAssetIsRewritten()
    {
        var settings = Settings;
        ProjectSettingsFiles.Create(settings, new PlayerSettings());
        var path = ProjectAsset.PathIn(settings.AssetsAbsoluteDir);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
        var before = ProjectSettingsMenu.Stamp(settings);

        ProjectSettingsFiles.Create(settings, new GraphicsSettings());

        Assert.NotEqual(before, ProjectSettingsMenu.Stamp(settings));
    }

    sealed class UndecoratedProjectSettings : ProjectSettingsAsset
    {
    }
}
