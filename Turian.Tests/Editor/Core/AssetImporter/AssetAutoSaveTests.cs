namespace Turian.Tests;

/// <summary>Tests for saving authored assets as they are edited, with no Apply step.</summary>
public class AssetAutoSaveTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"turian-autosave-{Guid.NewGuid():N}.dataasset");
    readonly AssetInspectionService inspections;

    /// <summary>The importer is only reached after the file is written; its failure is logged, not thrown.</summary>
    public AssetAutoSaveTests() =>
        inspections = new AssetInspectionService(importer: null!, new AssetTypeCatalog(), new SettingsService(),
            loader: null!, NullLogger.Instance);

    /// <summary>Deletes the written file.</summary>
    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
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
