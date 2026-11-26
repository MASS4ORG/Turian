namespace Turian.Tests;

/// <summary>An importer tells the asset database when an asset's source was imported again.</summary>
public sealed class AssetReimportSignalTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-reimport-{Guid.NewGuid():N}");
    readonly AssetDatabase database = new();
    readonly AssetImporter importer;
    readonly List<Guid> reimported = [];

    /// <summary>Creates an empty project with an importer on it.</summary>
    public AssetReimportSignalTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        var settings = new SettingsService();
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        importer = new AssetImporter(NullLogger.Instance, database, settings);
        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));
        database.AssetReimported += reimported.Add;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        importer.Dispose();
        Directory.Delete(project, recursive: true);
    }

    /// <summary>A source imported again raises the signal with its id, after the record is current.</summary>
    [Fact]
    public void ReimportRaisesTheSignal()
    {
        var source = Path.Combine(project, "Assets", "notes.txt");
        File.WriteAllText(source, "one");
        importer.ReimportNow(source);
        var id = Asset.Load($"{source}.meta")!.Id;

        // The project's file watcher may import the same write too, so only the first raise is pinned.
        Assert.NotEmpty(reimported);
        Assert.All(reimported.ToArray(), raised => Assert.Equal(id, raised));
        Assert.True(database.Assets.ContainsKey(id));

        var before = reimported.Count;
        File.WriteAllText(source, "two");
        importer.ReimportNow(source);

        Assert.True(reimported.Count > before);
    }

    /// <summary>The initial scan imports many assets without telling subscribers about each.</summary>
    [Fact]
    public void ScanDoesNotRaiseTheSignal()
    {
        File.WriteAllText(Path.Combine(project, "Assets", "scanned.txt"), "one");

        importer.GenerateMetaFiles(Path.Combine(project, "Assets"));

        Assert.Empty(reimported);
    }
}
