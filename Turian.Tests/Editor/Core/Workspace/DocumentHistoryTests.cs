namespace Turian.Tests.Editor;

/// <summary>A document's own undo stack, and the project's undo service seen as one.</summary>
[Collection(SerialTests.Name)]
public sealed class DocumentHistoryTests
{
    readonly List<string> ran = [];

    void Record(IDocumentHistory history, string name) =>
        history.Record(name, () => ran.Add($"undo {name}"), () => ran.Add($"redo {name}"));

    /// <summary>Steps undo newest first and redo in order, without running anything when recorded.</summary>
    [Fact]
    public void StepsUndoAndRedoInOrder()
    {
        var history = new DocumentHistory();
        Assert.False(history.CanUndo);
        Assert.Null(history.UndoLabel);
        Assert.Null(history.RedoLabel);
        history.Undo();
        history.Redo();
        Record(history, "a");
        Record(history, "b");
        Assert.Empty(ran);
        Assert.Equal("b", history.UndoLabel);

        history.Undo();
        history.Undo();
        Assert.Equal("a", history.RedoLabel);
        history.Redo();

        Assert.Equal(["undo b", "undo a", "redo a"], ran);
        Assert.True(history.CanUndo);
        Assert.True(history.CanRedo);

        Record(history, "c");
        Assert.False(history.CanRedo);

        history.Clear();
        Assert.False(history.CanUndo);
    }

    /// <summary>The oldest steps are dropped past the limit.</summary>
    [Fact]
    public void TheOldestStepsAreDropped()
    {
        var history = new DocumentHistory(limit: 1);
        Record(history, "a");
        Record(history, "b");

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.Equal(["undo b"], ran);
    }

    /// <summary>Changes a replayed step records on the way are not new steps.</summary>
    [Fact]
    public void ReplayingRecordsNothing()
    {
        var history = new DocumentHistory();
        history.Record("a", () => Record(history, "nested"), static () => { });

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.Equal("a", history.RedoLabel);
    }

    /// <summary>A step whose effect fails stays where it was.</summary>
    [Fact]
    public void AFailedStepStaysToBeTriedAgain()
    {
        var history = new DocumentHistory();
        var locked = true;
        history.Record("a", () =>
        {
            if (locked)
            {
                throw new IOException("locked");
            }
        }, static () => { });

        history.Undo();
        Assert.Equal("a", history.UndoLabel);
        Assert.False(history.CanRedo);

        locked = false;
        history.Undo();
        Assert.True(history.CanRedo);
    }

    /// <summary>A step that clears the history while it runs leaves it empty.</summary>
    [Fact]
    public void AStepMayClearTheHistory()
    {
        var history = new DocumentHistory();
        history.Record("a", history.Clear, static () => { });

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    /// <summary>A step recorded on the project's history is one of its steps, made once and replayed through it.</summary>
    [Fact]
    public void TheUndoServiceHistoryRecordsAnAlreadyMadeChange()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("one");
        var history = new UndoServiceHistory(() => harness.Undo, scene.AssetId);

        Record(history, "Rename");

        Assert.Empty(ran);
        Assert.True(history.CanUndo);
        Assert.Equal("Rename", history.UndoLabel);
        Assert.Equal(scene.AssetId, harness.Undo.History.UndoSteps[^1].Document);

        history.Undo();
        Assert.Equal("Rename", history.RedoLabel);
        Assert.True(history.CanRedo);
        history.Redo();
        Assert.Equal(["undo Rename", "redo Rename"], ran);

        history.Clear();
        Assert.False(history.CanUndo);
    }

    /// <summary>Clearing the project-wide view keeps every step, and one without a service is inert.</summary>
    [Fact]
    public void TheUndoServiceHistoryToleratesNoDocumentAndNoService()
    {
        using var harness = new SceneTabsHarness();
        harness.Open("one");
        var project = new UndoServiceHistory(() => harness.Undo);
        Record(project, "a");

        project.Clear();
        Assert.True(project.CanUndo);

        var none = new UndoServiceHistory(static () => null, Guid.NewGuid());
        Record(none, "b");
        none.Undo();
        none.Redo();
        none.Clear();
        Assert.False(none.CanUndo);
        Assert.False(none.CanRedo);
        Assert.Null(none.UndoLabel);
        Assert.Null(none.RedoLabel);
        Assert.Empty(ran);
    }
}
