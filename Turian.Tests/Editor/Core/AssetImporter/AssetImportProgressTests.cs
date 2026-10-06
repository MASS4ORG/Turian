namespace Turian.Tests;

/// <summary>Verifies that initial project imports report filenames and actual batch counters.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetImportProgressTests
{
    /// <summary>The monitoring scan reports import and indexing counts without an implicit unobserved constructor scan.</summary>
    [Fact]
    public void InitialImportReportsBothBatchPhases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-import-progress-{Guid.NewGuid():N}");
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        try
        {
            File.WriteAllText(Path.Combine(assets, "one.txt"), "one");
            File.WriteAllText(Path.Combine(assets, "two.txt"), "two");
            var settings = new SettingsService();
            var database = new AssetDatabase();
            using var importer = new AssetImporter(NullLogger.Instance, database, settings);
            importer.StartMonitoring();
            settings.Set(new AppSettings());
            importer.StartMonitoring();
            settings.Set(new AppSettings { ProjectAbsoluteDir = Path.Combine(root, "missing") });
            importer.StartMonitoring();
            settings.Set(new AppSettings { ProjectAbsoluteDir = root });
            var progress = Substitute.For<IProgressSink>();
            var importing = Substitute.For<IProgressScope>();
            var indexing = Substitute.For<IProgressScope>();
            progress.BeginChild(BackgroundTaskKind.Import, "Importing assets", 1).Returns(importing);
            progress.BeginChild(BackgroundTaskKind.Import, "Indexing assets", 1).Returns(indexing);
            importer.StartMonitoring(progress);
            importer.StartMonitoring(progress);
            importing.Received().Units(0, 2);
            importing.Received().Units(1, 2);
            importing.Received().Units(2, 2);
            importing.Received().Report(0, "one.txt");
            importing.Received().Report(0, "two.txt");
            indexing.Received().Units(0, 2);
            indexing.Received().Units(2, 2);
            importing.Received().Dispose();
            indexing.Received().Dispose();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
