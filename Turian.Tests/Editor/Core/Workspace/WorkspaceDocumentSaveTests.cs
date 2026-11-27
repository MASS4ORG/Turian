namespace Turian.Tests.Editor;

/// <summary>The save, close, conflict, read-only and reload rules every workspace document follows.</summary>
[Collection(SerialTests.Name)]
public sealed class WorkspaceDocumentSaveTests : IDisposable
{
    readonly DocumentProject project = new();

    AssetWorkspace Workspace => project.Workspace;

    /// <inheritdoc />
    public void Dispose() => project.Dispose();

    /// <summary>Each contributed document tracks and saves its own edits.</summary>
    [Fact]
    public void DocumentsAreDirtyIndependently()
    {
        var (first, one) = project.OpenFake("one");
        var (second, two) = project.OpenFake("two");
        var changes = 0;
        Workspace.Changed += () => changes++;

        one.Edit();

        Assert.True(changes > 0);
        Assert.True(first.IsDirty);
        Assert.Equal("*one", first.DisplayTitle);
        Assert.True(AssetWorkspace.NeedsSavePrompt(first));
        Assert.False(second.IsDirty);
        Assert.False(AssetWorkspace.NeedsSavePrompt(second));
        Assert.True(Workspace.HasUnsavedChanges);

        two.Edit();
        Assert.True(Workspace.Save(first).Succeeded);

        Assert.False(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Empty(two.Saves);
        Assert.True(Workspace.SaveAll());
        Assert.False(Workspace.HasUnsavedChanges);
        Assert.Equal([false], one.Saves);
        Assert.Empty(project.Reported());
    }

    /// <summary>A save that fails or throws leaves the document dirty and tells the user why.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedSaveStaysDirtyAndIsReported(bool throws)
    {
        var (tab, document) = project.OpenFake("one");
        var (_, other) = project.OpenFake("two");
        document.Edit();
        other.Edit();
        if (throws)
        {
            document.SaveException = new IOException("disk full");
        }
        else
        {
            document.SaveFailure = "disk full";
        }

        var result = Workspace.Save(tab);

        Assert.False(result.Succeeded);
        Assert.Equal("disk full", result.Error);
        Assert.True(tab.IsDirty);
        Assert.Contains("disk full", Assert.Single(project.Reported()), StringComparison.Ordinal);
        Assert.False(Workspace.SaveAll());
        Assert.False(other.IsDirty);
    }

    /// <summary>Closing with Save keeps the tab when the write fails and closes it once it succeeds.</summary>
    [Fact]
    public void CloseWithSaveKeepsTheTabUntilTheWriteSucceeds()
    {
        var (tab, document) = project.OpenFake("one");
        document.Edit();
        document.SaveFailure = "locked";

        Assert.False(Workspace.Close(tab, UnsavedChanges.Save));

        Assert.Same(tab, Assert.Single(Workspace.Documents));
        Assert.True(tab.IsDirty);
        Assert.False(document.Disposed);

        document.SaveFailure = null;
        Assert.True(Workspace.Close(tab, UnsavedChanges.Save));

        Assert.Empty(Workspace.Documents);
        Assert.Null(Workspace.Active);
        Assert.True(document.Disposed);
    }

    /// <summary>Discarding closes a dirty document without writing it.</summary>
    [Fact]
    public void CloseWithDiscardWritesNothing()
    {
        var (tab, document) = project.OpenFake("one");
        document.Edit();

        Assert.True(Workspace.Close(tab));

        Assert.Empty(document.Saves);
        Assert.True(document.Disposed);
    }

    /// <summary>A conflicted document is never overwritten unless the save asks to.</summary>
    [Fact]
    public void AConflictRefusesASilentOverwrite()
    {
        var (tab, document) = project.OpenFake("one");
        document.Edit();
        document.ConflictWith("changed on disk");

        Assert.Equal("changed on disk", tab.Conflict);
        Assert.Equal("changed on disk", Workspace.Save(tab).Error);
        Assert.False(Workspace.SaveAll());
        Assert.False(Workspace.Close(tab, UnsavedChanges.Save));
        Assert.Empty(document.Saves);
        Assert.True(tab.IsDirty);
        Assert.All(project.Reported(), static message => Assert.Contains("changed on disk", message,
            StringComparison.Ordinal));

        Assert.True(Workspace.Save(tab, overwrite: true).Succeeded);

        Assert.Equal([true], document.Saves);
        Assert.Null(tab.Conflict);
        Assert.False(tab.IsDirty);
    }

    /// <summary>Reloading resolves a conflict by dropping the edits and the steps that made them.</summary>
    [Fact]
    public void ReloadResolvesAConflictAndForgetsSteps()
    {
        var (tab, document) = project.OpenFake("one");
        document.Edit();
        document.ConflictWith("changed on disk");
        Assert.True(tab.History.CanUndo);

        Assert.True(Workspace.Reload(tab));

        Assert.Equal(1, document.Reloads);
        Assert.Null(tab.Conflict);
        Assert.False(tab.IsDirty);
        Assert.False(tab.History.CanUndo);
    }

    /// <summary>A source that cannot be read again keeps the edits and is reported.</summary>
    [Fact]
    public void AFailedReloadKeepsTheEditsAndIsReported()
    {
        var (tab, document) = project.OpenFake("one");
        document.Edit();
        document.ReloadFailure = new IOException("gone");

        Assert.False(Workspace.Reload(tab));

        Assert.True(tab.IsDirty);
        Assert.True(tab.History.CanUndo);
        Assert.Contains("gone", Assert.Single(project.Reported()), StringComparison.Ordinal);
    }

    /// <summary>A read-only document refuses to be written, whether the workspace or the brick says so.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadOnlyDocumentsRefuseSave(bool openedReadOnly)
    {
        var (tab, document) = project.OpenFake("one", openedReadOnly);
        Assert.Equal(openedReadOnly, document.Context.IsReadOnly);
        document.IsReadOnly = !openedReadOnly;
        Assert.True(tab.IsReadOnly);
        Assert.True(Workspace.Save(tab).Succeeded);

        document.Edit();

        Assert.Contains("read-only", Workspace.Save(tab, overwrite: true).Error, StringComparison.Ordinal);
        Assert.False(Workspace.SaveAll());
        Assert.False(Workspace.Close(tab, UnsavedChanges.Save));
        Assert.Empty(document.Saves);
        Assert.Same(tab, Assert.Single(Workspace.Documents));
        Assert.NotEmpty(project.Reported());
        Assert.True(Workspace.Close(tab));
    }

    /// <summary>Leaving the project is blocked while a contributed document cannot be written.</summary>
    [Fact]
    public void UnsavedWorkCoversContributedDocuments()
    {
        var (_, document) = project.OpenFake("one");
        var unsaved = new UnsavedWork(Workspace, new AssetAutoSave(new AssetInspectionService(null!,
            new AssetTypeCatalog(), project.Settings, null!, NullLogger.Instance)));
        document.Edit();
        document.SaveFailure = "locked";

        Assert.True(unsaved.Any);
        Assert.False(unsaved.SaveAll());

        document.SaveFailure = null;
        Assert.True(unsaved.SaveAll());
        Assert.False(unsaved.Any);

        document.Edit();
        unsaved.DiscardAll();
        Assert.True(document.Disposed);
        Assert.False(unsaved.Any);
    }

    /// <summary>An asset tab opened read-only is never written.</summary>
    [Fact]
    public void ReadOnlyAssetTabsRefuseSave()
    {
        var (path, _) = project.Source("level.prefab");
        var tab = Workspace.Open(path, readOnly: true)!;
        var saved = 0;
        project.Assets.AssetSaved += _ => saved++;
        Assert.True(tab.IsReadOnly);
        Assert.True(Workspace.Save(tab).Succeeded);

        project.Assets.AlterAsset(tab.Asset!);

        Assert.False(Workspace.Save(tab).Succeeded);
        Assert.False(Workspace.SaveAll());
        Assert.False(Workspace.Close(tab, UnsavedChanges.Save));
        Assert.Equal(0, saved);
        Assert.True(tab.IsDirty);
        Assert.NotEmpty(project.Reported());
    }

    /// <summary>An asset tab reports a write the asset writers could not complete.</summary>
    [Fact]
    public void AnAssetWriteThatFailsIsAFailedResult()
    {
        var (path, _) = project.Source("level.prefab");
        var tab = Workspace.Open(path)!;
        project.Assets.AlterAsset(tab.Asset!);

        // The scene writer marks an asset modified again when its file cannot be written.
        project.Assets.AssetSaved += static asset => asset.MarkModified();

        var result = Workspace.Save(tab);

        Assert.False(result.Succeeded);
        Assert.True(tab.IsDirty);
        Assert.Empty(project.Reported());
        Assert.True(Workspace.SaveAll());
        Assert.True(tab.IsDirty);
    }

    /// <summary>A tab built outside a workspace has nowhere to write to or read from.</summary>
    [Fact]
    public void ATabOutsideAWorkspaceCannotSave()
    {
        var asset = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/a.prefab" };
        using var tab = new AssetWrapper(asset);
        asset.MarkModified();

        tab.Reload();

        Assert.False(tab.Save().Succeeded);
        Assert.True(tab.IsDirty);
        Assert.Null(tab.SourcePath);
        Assert.Null(tab.Conflict);
        Assert.Equal(asset.Id.ToString(), tab.Id);
        Assert.Equal(WorkspaceDocumentKinds.Scene, tab.Kind);
        using var empty = new AssetWrapper(null);
        Assert.Equal("<assets>", empty.Title);
        Assert.Empty(empty.Id);
        Assert.Equal(WorkspaceDocumentKinds.Asset, empty.Kind);
    }

    /// <summary>A failed result must say why.</summary>
    [Fact]
    public void AFailedResultNeedsAReason()
    {
        Assert.Throws<ArgumentException>(static () => DocumentSaveResult.Failed(" "));
        Assert.True(DocumentSaveResult.Saved.Succeeded);
    }
}
