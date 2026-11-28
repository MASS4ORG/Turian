namespace Turian.Tests;

/// <summary>
/// The gate every read of the project from disk goes through: it writes the pending asset edits and the open documents,
/// and refuses, naming what is left, when one of them cannot be written.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class UnsavedWorkSaveBeforeTests : IDisposable
{
    readonly DocumentProject project = new();
    readonly BuildManager build;
    readonly AssetImporter importer;
    readonly AssetAutoSave autoSave;
    readonly UnsavedWork unsaved;

    /// <summary>Creates the gate over a workspace with a contributed document kind and a real importer.</summary>
    public UnsavedWorkSaveBeforeTests()
    {
        build = new BuildManager(new AppSettings { ProjectAbsoluteDir = project.Root }, NullLogger.Instance);
        importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), project.Settings, build);
        importer.StopMonitoring();
        var inspections = new AssetInspectionService(importer, new AssetTypeCatalog(), project.Settings,
            Substitute.For<IAssetLoader>(), NullLogger.Instance);
        autoSave = new AssetAutoSave(inspections, TimeSpan.FromHours(1));
        unsaved = new UnsavedWork(project.Workspace, autoSave, project.Notifications);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        importer.Dispose();
        build.Dispose();
        project.Dispose();
    }

    // An inspected payload waiting in the auto-save queue, written under the project's Assets folder.
    AssetInspection PendingEdit(DataAsset payload, string name)
    {
        var path = Path.Combine(project.Root, "Assets", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var meta = new DataAssetAsset { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}" };
        Serializer.Save($"{path}.meta", meta);
        return new AssetInspection(meta, path, payload, "Stats", IsPayload: true);
    }

    /// <summary>With nothing unsaved the gate passes and says nothing.</summary>
    [Fact]
    public void ACleanProjectPassesSilently()
    {
        project.OpenFake("menu");

        Assert.True(unsaved.SaveBefore("play"));

        Assert.Empty(project.Reported());
    }

    /// <summary>The open documents and the pending asset edits are both written before the gate passes.</summary>
    [Fact]
    public void SaveBeforeWritesTheDocumentsAndThePendingEdits()
    {
        var (_, document) = project.OpenFake("menu");
        document.Edit();
        var edit = PendingEdit(new ObjectReferencesTests.Stats { Health = 7 }, "stats.dataasset");
        autoSave.MarkChanged(edit);
        Assert.True(unsaved.Any);

        Assert.True(unsaved.SaveBefore("play"));

        Assert.Equal([false], document.Saves);
        Assert.False(document.IsDirty);
        Assert.False(autoSave.HasPending);
        Assert.Contains("7", File.ReadAllText(edit.AbsolutePath), StringComparison.Ordinal);
        Assert.Empty(project.Reported());
    }

    /// <summary>A document that stays dirty after its save refuses the action and the notice names it.</summary>
    [Fact]
    public void ADocumentStillDirtyAfterSaveRefusesAndIsNamed()
    {
        var (_, menu) = project.OpenFake("menu");
        menu.Edit();
        menu.StaysDirty = true;
        project.OpenFake("hud");

        Assert.False(unsaved.SaveBefore("export"));

        Assert.Contains("Cannot export: these documents are not saved: menu.", project.Reported());
    }

    /// <summary>A document whose write fails refuses the action, and the notice names it with the reason.</summary>
    [Fact]
    public void AFailedSaveRefusesAndNamesTheDocumentAndTheReason()
    {
        var (_, menu) = project.OpenFake("menu");
        menu.Edit();
        menu.SaveFailure = "locked";

        Assert.False(unsaved.SaveBefore("build and run"));

        var messages = project.Reported();
        Assert.Contains("Cannot build and run: these documents are not saved: menu.", messages);
        Assert.Contains(messages, static message => message.Contains("locked", StringComparison.Ordinal));
    }

    /// <summary>An asset edit that the asset writer rejects refuses the action instead of passing with the edit lost.</summary>
    [Fact]
    public void AnAssetEditThatCannotBeWrittenRefusesTheAction()
    {
        autoSave.MarkChanged(PendingEdit(
            new DataAssetValidationTests.ValidatedAsset { Label = "Starter", Points = 11 }, "invalid.dataasset"));

        Assert.False(unsaved.SaveBefore("play"));

        Assert.Contains("Cannot play: these documents are not saved: invalid.dataasset.", project.Reported());
        Assert.True(unsaved.Any);
        Assert.False(unsaved.SaveBefore("play"));
    }
}
