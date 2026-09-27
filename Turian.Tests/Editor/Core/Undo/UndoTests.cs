namespace Turian.Tests;

/// <summary>Tests for snapshots, the per-document history and the undo service the studio drives.</summary>
public class UndoTests
{
    readonly AssetManager assets = new();
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly UndoService undo;
    readonly Prefab scene = new() { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
    readonly Node root = new() { Name = "Root" };

    /// <summary>Opens a scene with no file behind it; the importer is never reached.</summary>
    public UndoTests()
    {
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!);
        inspector = new NodeInspectorController(assets);
        undo = new UndoService(sceneTree, inspector, assets);

        assets.OpenAsset(scene);
        sceneTree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
    }

    Node AddChild(string name)
    {
        var child = new Node { Name = name };
        root.Children.Add(child);
        child.Parent = root;
        return child;
    }

    // What the studio does after any edit: the scene is marked altered, then the frame ends.
    void EndFrame()
    {
        assets.AlterAsset(scene);
        undo.Flush();
    }

    /// <summary>A component's values come back from its snapshot, and a copy never tracks later edits.</summary>
    [Fact]
    public void ObjectState_RestoresValues()
    {
        var light = new LightComponent { Intensity = 2f };
        var state = ObjectState.Capture(light);

        light.Intensity = 9f;
        Assert.False(state.SameAs(ObjectState.Capture(light)));

        state.Restore(light);
        Assert.Equal(2f, light.Intensity);
        Assert.True(state.SameAs(ObjectState.Capture(light)));
    }

    /// <summary>A node's transform is copied, not shared, so moving the node leaves the snapshot alone.</summary>
    [Fact]
    public void ObjectState_CopiesTransform()
    {
        var node = new Node { Name = "Box" };
        var state = ObjectState.Capture(node);

        node.Transform.Position = new Vector3(4f, 0f, 0f);
        state.Restore(node);

        Assert.Equal(Vector3.Zero, node.Transform.Position);
    }

    /// <summary>A node's children come back in their order, with their parent set.</summary>
    [Fact]
    public void ObjectState_RestoresChildren()
    {
        var first = AddChild("First");
        var second = AddChild("Second");
        var state = ObjectState.Capture(root);

        root.Children.Remove(first);
        first.Parent = null;
        root.Children.Add(new Node { Name = "Third" });
        state.Restore(root);

        Assert.Equal([first, second], root.Children);
        Assert.Same(root, first.Parent);
    }

    /// <summary>Consecutive edits to the same objects join one step; a pause or another label starts a new one.</summary>
    [Fact]
    public void UndoHistory_MergesContinuousEdits()
    {
        var node = new Node { Name = "Box" };
        var history = new UndoHistory(mergeWindow: TimeSpan.FromSeconds(1));
        UndoStep Step(string label, DateTime at) => new(label,
            new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance) { [node] = ObjectState.Capture(node) },
            new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance) { [node] = ObjectState.Capture(node) })
        { LastChanged = at };

        var start = DateTime.UtcNow;
        history.Push(Step("Move", start));
        history.Push(Step("Move", start.AddMilliseconds(500)));
        history.Push(Step("Move", start.AddMilliseconds(900)));
        Assert.Single(history.UndoSteps);

        history.Push(Step("Move", start.AddSeconds(5)));
        history.Push(Step("Rename", start.AddSeconds(5.1)));
        Assert.Equal(3, history.UndoSteps.Count);
    }

    /// <summary>An edit to the selected node is undone and redone.</summary>
    [Fact]
    public void Undo_RevertsAnEditToTheSelection()
    {
        var box = AddChild("Box");
        inspector.Select(box);

        box.Name = "Crate";
        EndFrame();
        Assert.Equal("Undo Edit Crate", $"Undo {undo.UndoLabel}");

        undo.Undo();
        Assert.Equal("Box", box.Name);

        undo.Redo();
        Assert.Equal("Crate", box.Name);
    }

    /// <summary>A deleted node comes back under its parent, and redo removes it again.</summary>
    [Fact]
    public void Undo_BringsBackADeletedNode()
    {
        var box = AddChild("Box");

        undo.RecordObject(root, "Delete");
        sceneTree.DetachNode(box);
        EndFrame();

        undo.Undo();
        Assert.Same(root, box.Parent);
        Assert.Contains(box, root.Children);

        undo.Redo();
        Assert.DoesNotContain(box, root.Children);
    }

    /// <summary>Undoing the creation of the selected node clears the selection.</summary>
    [Fact]
    public void Undo_OfCreatedSelection_ClearsSelection()
    {
        undo.RecordObject(root, "Create Node");
        var created = AddChild("Node");
        EndFrame();
        inspector.Select(created);

        undo.Undo();

        Assert.Null(inspector.SelectedNode);
        Assert.Empty(root.Children);
    }

    /// <summary>An added component is removed by undo, with the required ones it brought.</summary>
    [Fact]
    public void Undo_RemovesAnAddedComponent()
    {
        var box = AddChild("Box");
        inspector.Select(box);

        undo.RecordObject(box, "Add Component");
        inspector.AddComponent(typeof(LightComponent));
        undo.Flush();

        undo.Undo();
        Assert.Empty(box.Components);
    }

    /// <summary>Nothing is recorded while Play Mode shows the running scene.</summary>
    [Fact]
    public void PlayMode_IsNotRecorded()
    {
        var running = new Node { Name = "Running" };
        sceneTree.ShowRuntimeScene(running);
        inspector.Select(running);

        running.Name = "Changed";
        EndFrame();

        Assert.False(undo.CanUndo);
    }

    /// <summary>An inspected data asset has its own history, and its edits never touch the scene's.</summary>
    [Fact]
    public void Undo_RevertsAnInspectedAssetEdit()
    {
        var stats = new ObjectReferencesTests.Stats { Health = 10 };
        var metadata = new DataAssetAsset { Id = Guid.NewGuid(), RelativePath = "Assets/stats.dataasset" };
        inspector.Select(new AssetInspection(metadata, "/tmp/stats.dataasset", stats, "Stats", IsPayload: true));

        stats.Health = 25;
        undo.MarkAltered();
        undo.Flush();
        Assert.True(undo.CanUndo);

        undo.Undo();
        Assert.Equal(10, stats.Health);

        inspector.ClearSelection();
        Assert.False(undo.CanUndo);
    }
}
