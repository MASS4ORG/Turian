namespace Turian.Tests;

/// <summary>Bricks contribute Create entries that write their own sources through <see cref="AssetCreationKind.Creator"/>.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetCreationCreatorTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-asset-creator-").FullName;
    readonly SettingsService settings = new();
    readonly BuildManager build;
    readonly AssetImporter importer;
    readonly ILogger logger = Substitute.For<ILogger>();

    sealed class Provider(params AssetCreationKind[] kinds) : IAssetCreationProvider
    {
        public IReadOnlyList<AssetCreationKind> Kinds { get; } = kinds;
    }

    /// <summary>Opens a project the catalog can write into.</summary>
    public AssetCreationCreatorTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        var app = new AppSettings { ProjectAbsoluteDir = project };
        settings.Set(app);
        build = new BuildManager(app, logger);
        importer = new AssetImporter(logger, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        importer.Dispose();
        build.Dispose();
        Directory.Delete(project, recursive: true);
    }

    AssetCreationCatalog Catalog(params AssetCreationKind[] contributed) =>
        new(new AssetFileSystem(settings, importer), settings, build, logger, [new Provider(contributed)]);

    /// <summary>Several creator entries with no payload type all reach the menu, in the order contributed.</summary>
    [Fact]
    public void CreatorKindsAreListedEvenWithoutAPayloadType()
    {
        using var catalog = Catalog(
            new AssetCreationKind("UI Document", "NewUI.ui") { Creator = static _ => "a" },
            new AssetCreationKind("UI Style Sheet", "NewStyle.pss") { Creator = static _ => "b" },
            new AssetCreationKind("UI Document", "Duplicate.ui") { Creator = static _ => "c" });

        var paths = catalog.Kinds.Select(static kind => kind.MenuPath).ToList();

        Assert.Equal(["UI Document", "UI Style Sheet"], paths.Where(static path => path.StartsWith("UI", StringComparison.Ordinal)));
        Assert.Equal("NewUI.ui", catalog.Kinds.Single(static kind => kind.MenuPath == "UI Document").DefaultName);
    }

    /// <summary>The creator receives the target folder and decides success by returning the new path.</summary>
    [Fact]
    public void CreateRunsTheCreatorInTheTargetDirectory()
    {
        var assets = Path.Combine(project, "Assets");
        string? received = null;
        var created = new AssetCreationKind("Notes", "Notes.txt")
        {
            Creator = directory =>
            {
                received = directory;
                var path = Path.Combine(directory, "Notes.txt");
                File.WriteAllText(path, "hello");
                return path;
            },
        };
        var failing = new AssetCreationKind("Broken", "Broken.txt") { Creator = static _ => null };
        var throwing = new AssetCreationKind("Throws", "Throws.txt")
        {
            Creator = static _ => throw new IOException("disk full"),
        };
        using var catalog = Catalog(created, failing, throwing);

        Assert.True(catalog.Create(created, assets));
        Assert.Equal(assets, received);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(assets, "Notes.txt")));
        Assert.False(catalog.Create(failing, assets));
        Assert.False(catalog.Create(throwing, assets));
    }

    /// <summary>Existing payload-typed entries still deduplicate by type.</summary>
    [Fact]
    public void PayloadKindsStillDeduplicateByType()
    {
        using var catalog = Catalog(
            new AssetCreationKind("Settings/Player", "A.dataasset", typeof(PlayerSettings)),
            new AssetCreationKind("Settings/Player copy", "B.dataasset", typeof(PlayerSettings)));

        Assert.Single(catalog.Kinds, static kind => kind.PayloadType == typeof(PlayerSettings));
    }
}
