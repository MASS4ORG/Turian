namespace Turian.Tests;

/// <summary>Checks the question asked when the user leaves a project with unsaved documents.</summary>
[Collection(SerialTests.Name)]
public sealed class UnsavedChangesGuardTests : IDisposable
{
    readonly AssetBrowserHarness harness = new();
    readonly DocumentProject project = new();
    readonly UnsavedChangesDialogChrome dialog = new(new StudioLocalization());
    readonly UnsavedChangesGuard guard;
    int left;

    /// <summary>Creates the guard over the real dialog and a workspace with a contributed document kind.</summary>
    public UnsavedChangesGuardTests()
    {
        var unsaved = new UnsavedWork(project.Workspace, new AssetAutoSave(
            new AssetInspectionService(null!, new AssetTypeCatalog(), project.Settings, null!, NullLogger.Instance)));
        guard = new UnsavedChangesGuard(unsaved, dialog, new StudioLocalization());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        project.Dispose();
        harness.Dispose();
    }

    void Frame() => harness.Frame(dialog.Render);

    // The footer holds Save, Don't Save and Cancel in that order. The dialog re-centers once it has measured itself.
    void Press(int button)
    {
        Frame();
        Frame();
        Frame();
        var footer = harness.Nodes().Single(static node => node.Children.Count == 3
            && node.Id.Contains(nameof(UnsavedChangesDialogChrome), StringComparison.Ordinal));
        harness.Input.MousePosition.Returns(footer.Children[button].Rect.Center);
        Frame();
        harness.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        harness.Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        harness.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        harness.Input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame();
    }

    void Leave() => guard.Leave("Save them?", () => left++);

    /// <summary>With nothing unsaved the project is left at once.</summary>
    [Fact]
    public void ACleanProjectIsLeftWithoutAQuestion()
    {
        project.OpenFake("menu");

        Assert.False(guard.HasUnsavedChanges);
        Leave();

        Assert.Equal(1, left);
    }

    /// <summary>Save writes the documents before leaving, Don't Save drops them, and Cancel keeps everything.</summary>
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 0, 0)]
    public void TheChoiceDecidesWhatHappensToTheDocuments(int button, int leaves, int saves)
    {
        var (_, document) = project.OpenFake("menu");
        document.Edit();
        Assert.True(guard.HasUnsavedChanges);

        Leave();
        Assert.Equal(0, left);
        Press(button);

        Assert.Equal(leaves, left);
        Assert.Equal(saves, document.Saves.Count);
        Assert.Equal(button == 1, document.Disposed);
        Assert.Equal(button == 2, guard.HasUnsavedChanges);
    }

    /// <summary>A document that cannot be written keeps the project open with its edits.</summary>
    [Fact]
    public void AFailedSaveKeepsTheProjectOpen()
    {
        var (tab, document) = project.OpenFake("menu");
        document.Edit();
        document.SaveFailure = "locked";

        Leave();
        Press(0);

        Assert.Equal(0, left);
        Assert.True(tab.IsDirty);
        Assert.Same(tab, Assert.Single(project.Workspace.Documents));
    }
}
