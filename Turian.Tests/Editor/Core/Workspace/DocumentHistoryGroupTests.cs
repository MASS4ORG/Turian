namespace Turian.Tests.Editor;

/// <summary>Changes recorded in a group are one undo step, in a document's own history and in the project's.</summary>
[Collection(SerialTests.Name)]
public sealed class DocumentHistoryGroupTests
{
    readonly List<string> ran = [];

    void Record(IDocumentHistory history, string name) =>
        history.Record(name, () => ran.Add($"undo {name}"), () => ran.Add($"redo {name}"));

    /// <summary>A group is one step under its label, undone newest change first and redone in order.</summary>
    [Fact]
    public void AGroupIsOneStep()
    {
        var history = new DocumentHistory();
        Record(history, "before");
        using (history.BeginGroup("Edit 2"))
        {
            Record(history, "a");
            Assert.Equal("Edit 2", history.UndoLabel);
            Record(history, "b");
        }

        history.Undo();

        Assert.Equal(["undo b", "undo a"], ran);
        Assert.Equal("before", history.UndoLabel);
        Assert.Equal("Edit 2", history.RedoLabel);
        ran.Clear();
        history.Redo();
        Assert.Equal(["redo a", "redo b"], ran);
    }

    /// <summary>A nested group joins the outer one, and changes after both are their own step.</summary>
    [Fact]
    public void NestedGroupsJoinTheOuterOne()
    {
        var history = new DocumentHistory();
        var outer = history.BeginGroup("outer");
        Record(history, "a");
        using (history.BeginGroup("inner"))
        {
            Record(history, "b");
        }

        Record(history, "c");
        outer.Dispose();
        outer.Dispose();
        Record(history, "d");

        Assert.Equal("d", history.UndoLabel);
        history.Undo();
        Assert.Equal("outer", history.UndoLabel);
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(["undo d", "undo c", "undo b", "undo a"], ran);
    }

    /// <summary>A group without changes records nothing and keeps what could be redone.</summary>
    [Fact]
    public void AnEmptyGroupRecordsNothing()
    {
        var history = new DocumentHistory();
        Record(history, "a");
        history.Undo();

        history.BeginGroup("empty").Dispose();

        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Throws<ArgumentException>(() => history.BeginGroup(" "));
    }

    /// <summary>Undoing inside an open group ends the step it collected, so later changes make a new one.</summary>
    [Fact]
    public void AStepUndoneWhileItsGroupIsOpenIsNotExtended()
    {
        var history = new DocumentHistory();
        using (history.BeginGroup("group"))
        {
            Record(history, "a");
            history.Undo();
            Record(history, "b");
            Record(history, "c");
        }

        ran.Clear();
        history.Undo();

        Assert.Equal(["undo c", "undo b"], ran);
        Assert.False(history.CanUndo);
    }

    /// <summary>A change that fails to replay leaves its whole step in place, the other changes as before.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedChangeRestoresTheRestOfItsStep(bool undoing)
    {
        var history = new DocumentHistory();
        var locked = false;
        void Locked()
        {
            if (locked)
            {
                throw new IOException("locked");
            }
        }

        using (history.BeginGroup("group"))
        {
            Record(history, "a");
            history.Record("failing", Locked, Locked);
            history.Record("c", () => ran.Add("undo c"), () => ran.Add("redo c"));
        }

        if (!undoing)
        {
            history.Undo();
        }

        ran.Clear();
        locked = true;
        if (undoing)
        {
            history.Undo();
            Assert.Equal(["undo c", "redo c"], ran);
            Assert.True(history.CanUndo);
        }
        else
        {
            history.Redo();
            Assert.Equal(["redo a", "undo a"], ran);
            Assert.True(history.CanRedo);
        }
    }

    /// <summary>A change that cannot even be restored is logged, and the failure still keeps the step.</summary>
    [Fact]
    public void AFailedRestoreIsContained()
    {
        var history = new DocumentHistory();
        using (history.BeginGroup("group"))
        {
            history.Record("a", static () => throw new IOException("first"), static () => { });
            history.Record("b", static () => { }, static () => throw new IOException("second"));
        }

        history.Undo();

        Assert.True(history.CanUndo);
    }

    /// <summary>A group in the project's history is one step of its undo service, named by the outer group.</summary>
    [Fact]
    public void TheUndoServiceHistoryGroupsIntoOneStep()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("one");
        var history = new UndoServiceHistory(() => harness.Undo, scene.AssetId);
        Record(history, "before");

        using (history.BeginGroup("Edit 2"))
        {
            Record(history, "a");
            using (history.BeginGroup("inner"))
            {
                Record(history, "b");
            }

            Record(history, "c");
        }

        Assert.Equal(2, harness.Undo.History.UndoSteps.Count);
        Assert.Equal("Edit 2", history.UndoLabel);
        ran.Clear();
        history.Undo();
        Assert.Equal(["undo c", "undo b", "undo a"], ran);
        ran.Clear();
        history.Redo();
        Assert.Equal(["redo a", "redo b", "redo c"], ran);
        Record(history, "after");
        Assert.Equal("after", history.UndoLabel);
        Assert.Equal(3, harness.Undo.History.UndoSteps.Count);
    }

    /// <summary>Changes of different documents stay separate steps even within one gesture.</summary>
    [Fact]
    public void AGestureKeepsDocumentsApart()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("one");
        var first = new UndoServiceHistory(() => harness.Undo, scene.AssetId);
        var other = new UndoServiceHistory(() => harness.Undo, Guid.NewGuid());

        using (first.BeginGroup("first"))
        {
            Record(first, "a");
            Record(other, "b");
        }

        Assert.Equal(2, harness.Undo.History.UndoSteps.Count);
    }

    /// <summary>A gesture joins only its own changes: an earlier step, or a later gesture, is never extended.</summary>
    [Fact]
    public void AGestureNeverJoinsAStepOutsideIt()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("one");
        var history = new UndoServiceHistory(() => harness.Undo, scene.AssetId);
        Record(history, "before");

        using (history.BeginGroup("first"))
        {
            Record(history, "a");
        }

        using (history.BeginGroup("second"))
        {
            Record(history, "b");
        }

        Assert.Equal(3, harness.Undo.History.UndoSteps.Count);
        Assert.Equal("second", history.UndoLabel);
        Record(history, "c");
        Assert.Equal(4, harness.Undo.History.UndoSteps.Count);
        Assert.Equal("c", history.UndoLabel);
    }

    /// <summary>A change that keeps objects' values joins a gesture too.</summary>
    [Fact]
    public void AChangeThatKeepsValuesJoinsTheGesture()
    {
        using var harness = new SceneTabsHarness();
        var scene = harness.Open("one");
        harness.Undo.BeginGesture();
        harness.Undo.Perform("a", [], () => ran.Add("do a"), () => ran.Add("undo a"), keepValues: true,
            document: scene.AssetId);
        harness.Undo.Perform("b", [], () => ran.Add("do b"), () => ran.Add("undo b"), keepValues: true,
            document: scene.AssetId);
        harness.Undo.EndGesture();

        ran.Clear();
        harness.Undo.Undo();

        Assert.Equal(["undo b", "undo a"], ran);
        Assert.False(harness.Undo.CanUndo);
    }

    /// <summary>Without a service a group is inert, and a history with one step cannot join it with another.</summary>
    [Fact]
    public void GroupsTolerateNothingToJoin()
    {
        var none = new UndoServiceHistory(static () => null);
        using (none.BeginGroup("nothing"))
        {
            Record(none, "a");
        }

        var history = new UndoHistory();
        history.JoinLatest();
        Assert.False(history.CanUndo);
        Assert.Throws<ArgumentException>(() => none.BeginGroup(""));
    }
}
