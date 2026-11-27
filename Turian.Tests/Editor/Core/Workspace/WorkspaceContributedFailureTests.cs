namespace Turian.Tests;

/// <summary>A brick's code failing in any way stays inside the workspace, and reopening keeps a tab writable.</summary>
public sealed class WorkspaceContributedFailureTests : IDisposable
{
    readonly DocumentProject project = new();

    AssetWorkspace Workspace => project.Workspace;

    /// <inheritdoc />
    public void Dispose() => project.Dispose();

    /// <summary>Asking for a read-only open of an editable open source only brings it to the front.</summary>
    [Theory]
    [InlineData("level.prefab")]
    [InlineData("menu.fake")]
    public void AReadOnlyOpenOfAnEditableTabOnlyActivatesIt(string name)
    {
        var (path, _) = project.Source(name);
        var tab = Workspace.Open(path)!;
        Workspace.Open(project.Source("other.prefab").Path);

        var again = Workspace.Open(path, readOnly: true);

        Assert.Same(tab, again);
        Assert.Same(tab, Workspace.Active);
        Assert.False(tab.IsReadOnly);
    }

    /// <summary>Any exception from a save fails it, keeps the document dirty and is reported.</summary>
    [Theory]
    [InlineData("exploded", "exploded")]
    [InlineData("", nameof(NotImplementedException))]
    public void AnyExceptionFromASaveIsAFailedResult(string message, string reported)
    {
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        document.SaveException = new NotImplementedException(message);

        var result = Workspace.Save(tab);

        Assert.False(result.Succeeded);
        Assert.True(tab.IsDirty);
        Assert.Contains(reported, Assert.Single(project.Reported()));
    }

    /// <summary>A save that returns nothing, or a dirty state that throws, is a failed result too.</summary>
    [Fact]
    public void ABrokenSaveContractIsAFailedResult()
    {
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        document.SavesNothing = true;
        Assert.False(Workspace.Save(tab).Succeeded);

        document.SavesNothing = false;
        document.DirtyFailure = new NotSupportedException("state");
        Assert.False(tab.Save().Succeeded);
        document.DirtyFailure = null;
    }

    /// <summary>Any exception from a reload is reported by the workspace and swallowed by the tab itself.</summary>
    [Fact]
    public void AnyExceptionFromAReloadStaysInside()
    {
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        document.ReloadFailure = new FormatException("unreadable");

        Assert.False(Workspace.Reload(tab));
        tab.Reload();

        Assert.True(tab.IsDirty);
        Assert.True(tab.History.CanUndo);
        Assert.Contains("unreadable", Assert.Single(project.Reported()));
    }

    /// <summary>A replayed step that throws anything stays in place to be tried again.</summary>
    [Fact]
    public void AnyExceptionFromAReplayKeepsTheStep()
    {
        var history = new DocumentHistory();
        history.Record("Break", static () => throw new NotImplementedException(),
            static () => { });

        history.Undo();

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    /// <summary>A kind that returns no document opens nothing and is reported.</summary>
    [Fact]
    public void AKindThatReturnsNothingOpensNothing()
    {
        project.Kind.OpensNothing = true;

        Assert.Null(Workspace.Open(project.Source("menu.fake").Path));

        Assert.Empty(Workspace.Documents);
        Assert.Contains("returned no document", Assert.Single(project.Reported()));
    }

    /// <summary>A document that throws while closing still leaves the workspace.</summary>
    [Fact]
    public void AThrowingDisposeStillClosesTheTab()
    {
        var (tab, document) = project.OpenFake("menu");
        document.DisposeFailure = new NotSupportedException("stuck");

        Assert.True(Workspace.Close(tab));

        Assert.True(document.Disposed);
        Assert.Empty(Workspace.Documents);
        Assert.Null(Workspace.Active);
    }
}
