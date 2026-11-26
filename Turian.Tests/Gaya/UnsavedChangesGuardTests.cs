namespace Turian.Tests;

/// <summary>Checks the question asked when a document editor closes with unsaved edits.</summary>
[Collection(SerialTests.Name)]
public sealed class UnsavedChangesGuardTests : IDisposable
{
    readonly AssetBrowserHarness harness = new();
    readonly UnsavedChangesDialogChrome dialog = new(new StudioLocalization());
    readonly UnsavedChangesGuard guard;
    readonly AssetDocumentHostTests.DocumentFixture session = new();

    /// <summary>Creates the guard over the real dialog.</summary>
    public UnsavedChangesGuardTests()
    {
        var unsaved = new UnsavedWork(harness.Workspace, new AssetAutoSave(
            new AssetInspectionService(null!, new AssetTypeCatalog(), harness.Settings, null!, NullLogger.Instance)));
        guard = new UnsavedChangesGuard(unsaved, dialog, new StudioLocalization());
    }

    /// <inheritdoc />
    public void Dispose() => harness.Dispose();

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

    /// <summary>Save and Don't Save report the choice and never the cancel callback.</summary>
    [Theory]
    [InlineData(0, UnsavedChanges.Save)]
    [InlineData(1, UnsavedChanges.Discard)]
    public void ChoicesAreReported(int button, UnsavedChanges expected)
    {
        UnsavedChanges? choice = null;
        var cancelled = 0;
        guard.AskClose(session, answer => choice = answer, () => cancelled++);

        Press(button);

        Assert.Equal(expected, choice);
        Assert.Equal(0, cancelled);
    }

    /// <summary>Cancel reports a cancel exactly once and no choice.</summary>
    [Fact]
    public void CancelIsReported()
    {
        UnsavedChanges? choice = null;
        var cancelled = 0;
        guard.AskClose(session, answer => choice = answer, () => cancelled++);

        Press(2);
        Frame();

        Assert.Null(choice);
        Assert.Equal(1, cancelled);
    }

    /// <summary>A new question while one is open cancels the earlier one.</summary>
    [Fact]
    public void ANewQuestionCancelsThePendingOne()
    {
        var cancelled = 0;
        guard.AskClose(session, _ => { }, () => cancelled++);
        guard.AskClose(session, _ => { }, () => cancelled += 10);

        Assert.Equal(1, cancelled);
    }
}
