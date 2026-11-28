namespace Turian.Tests;

/// <summary>Tests for saving authored assets as they are edited, with no Apply step.</summary>
public class AssetAutoSaveTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-autosave-").FullName;
    readonly string path;
    readonly BuildManager build;
    readonly AssetImporter importer;
    readonly AssetInspectionService inspections;

    /// <summary>Creates the inspection service over a real importer in a temporary folder.</summary>
    public AssetAutoSaveTests()
    {
        path = Path.Combine(root, "stats.dataasset");
        build = new BuildManager(new AppSettings { ProjectAbsoluteDir = root }, NullLogger.Instance);
        var settings = new SettingsService();
        importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
        inspections = new AssetInspectionService(importer, new AssetTypeCatalog(), settings,
            Substitute.For<IAssetLoader>(), NullLogger.Instance);
    }

    /// <summary>Deletes the temporary folder.</summary>
    public void Dispose()
    {
        importer.Dispose();
        build.Dispose();
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    AssetInspection Inspection(bool isPayload, object target) =>
        new(new DataAssetAsset { Id = Guid.NewGuid() }, path, target, "Stats", isPayload);

    /// <summary>An edit waits for the user to pause, then is written.</summary>
    [Fact]
    public void Flush_WritesOnceEditsSettle()
    {
        var autoSave = new AssetAutoSave(inspections, TimeSpan.FromHours(1));
        autoSave.MarkChanged(Inspection(isPayload: true, new ObjectReferencesTests.Stats { Health = 42 }));

        autoSave.Flush();
        Assert.True(autoSave.HasPending);
        Assert.False(File.Exists(path));

        autoSave.Flush(force: true);
        Assert.False(autoSave.HasPending);
        Assert.Contains("42", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>Import settings keep their Apply step, so they are never saved here.</summary>
    [Fact]
    public void MarkChanged_IgnoresImportSettings()
    {
        var autoSave = new AssetAutoSave(inspections);

        autoSave.MarkChanged(Inspection(isPayload: false, new object()));

        Assert.False(autoSave.HasPending);
    }

    /// <summary>Choosing not to save drops the pending edits; nothing is written.</summary>
    [Fact]
    public void UnsavedWork_DiscardAll_WritesNothing()
    {
        var autoSave = new AssetAutoSave(inspections, TimeSpan.FromHours(1));
        using var workspace = new AssetWorkspace(new AssetManager(), new SettingsService());
        var unsaved = new UnsavedWork(workspace, autoSave);
        autoSave.MarkChanged(Inspection(isPayload: true, new ObjectReferencesTests.Stats { Health = 7 }));
        Assert.True(unsaved.Any);

        unsaved.DiscardAll();
        autoSave.Flush(force: true);

        Assert.False(unsaved.Any);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// An edit the writer refuses stays unsaved work: the frame flush does not retry it, a forced flush does and reports
    /// it, and a new edit of the asset replaces it.
    /// </summary>
    [Fact]
    public void Flush_KeepsARefusedEditUntilItIsWrittenOrReplaced()
    {
        var autoSave = new AssetAutoSave(inspections, TimeSpan.Zero);
        autoSave.MarkChanged(Inspection(isPayload: true, new object()));

        Assert.False(autoSave.Flush());
        Assert.Equal(path, Assert.Single(autoSave.Rejected));
        Assert.True(autoSave.HasPending);
        Assert.False(autoSave.Flush());
        Assert.False(autoSave.Flush(force: true));

        autoSave.MarkChanged(Inspection(isPayload: true, new ObjectReferencesTests.Stats { Health = 9 }));
        Assert.Empty(autoSave.Rejected);
        Assert.True(autoSave.Flush());
        Assert.False(autoSave.HasPending);
        Assert.Contains("9", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>Choosing not to save also forgets an edit the writer refused.</summary>
    [Fact]
    public void Discard_ForgetsARefusedEdit()
    {
        var autoSave = new AssetAutoSave(inspections, TimeSpan.Zero);
        autoSave.MarkChanged(Inspection(isPayload: true, new object()));
        autoSave.Flush();

        autoSave.Discard();

        Assert.False(autoSave.HasPending);
        Assert.Empty(autoSave.Rejected);
    }

    /// <summary>Choosing to save writes the pending edits at once.</summary>
    [Fact]
    public void UnsavedWork_SaveAll_WritesPendingEdits()
    {
        var autoSave = new AssetAutoSave(inspections, TimeSpan.FromHours(1));
        using var workspace = new AssetWorkspace(new AssetManager(), new SettingsService());
        autoSave.MarkChanged(Inspection(isPayload: true, new ObjectReferencesTests.Stats { Health = 7 }));

        new UnsavedWork(workspace, autoSave).SaveAll();

        Assert.Contains("7", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
