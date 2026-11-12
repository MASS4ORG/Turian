namespace Turian.Tests;

/// <summary>A meta file names its source relative to the project, with forward slashes, on every system.</summary>
public sealed class MetaFilePathTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-meta-path-{Guid.NewGuid():N}");
    readonly SettingsService settings = new();
    readonly AssetImporter importer;

    /// <summary>Creates an empty project with an importer on it.</summary>
    public MetaFilePathTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets", "Scenes"));

        // Meta files do not depend on the renderer brick, whose packages every new project would otherwise restore.
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        importer.Dispose();
        Directory.Delete(project, recursive: true);
    }

    string Source(string name)
    {
        var path = Path.Combine(project, "Assets", "Scenes", name);
        File.WriteAllText(path, "text");
        return path;
    }

    static string StoredPath(string source) =>
        JsonNode.Parse(File.ReadAllText($"{source}.meta"))![nameof(Asset.RelativePath)]!.GetValue<string>();

    /// <summary>A meta the importer creates stores the project-relative path, not the absolute one it worked with.</summary>
    [Fact]
    public void CreatedMetasStoreProjectRelativePaths()
    {
        var source = Source("notes.txt");

        importer.ReimportNow(source);

        Assert.Equal("Assets/Scenes/notes.txt", StoredPath(source));
    }

    /// <summary>A meta carrying a machine's path, from any system, is repaired without changing the asset's id.</summary>
    [Theory]
    [InlineData(@"C:\\Users\\someone\\project\\Assets\\Scenes\\notes.txt")]
    [InlineData("/media/work/project/Assets/Scenes/notes.txt")]
    [InlineData(@"Assets\\Scenes\\notes.txt")]
    public void MachineSpecificPathsAreRepairedOnScan(string stored)
    {
        // The meta lands first, as a checkout would write it: a source alone lets the watcher create a fresh meta.
        var source = Path.Combine(project, "Assets", "Scenes", "notes.txt");
        var id = Guid.NewGuid();
        File.WriteAllText($"{source}.meta",
            $$"""{ "__TypeId": "2af5e114-fb34-54c7-8f14-04aed6c7eb74", "RelativePath": "{{stored}}", "Labels": [], "Id": "{{id}}" }""");
        Source("notes.txt");

        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        Assert.Equal("Assets/Scenes/notes.txt", StoredPath(source));
        Assert.Equal(id, Asset.Load($"{source}.meta")!.Id);
    }

    /// <summary>An editor's save-by-rename leaves the asset's meta, and so its id, where it is.</summary>
    [Fact]
    public void TemporarySaveRenamesKeepTheMeta()
    {
        var source = Source("rain.prefab");
        importer.ReimportNow(source);
        var meta = $"{source}.meta";
        var id = Asset.Load(meta)!.Id;
        var temporary = $"{source}~RFb9313ca.TMP";

        File.Move(source, temporary);
        typeof(AssetImporter).GetMethod("HandleRenamedPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(importer, [source, temporary]);

        Assert.True(File.Exists(meta));
        Assert.False(File.Exists($"{temporary}.meta"));
        Assert.Equal(id, Asset.Load(meta)!.Id);
    }
}
