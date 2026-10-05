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
        var loader = Substitute.For<IAssetLoader>();
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!,
            assetLoader: loader, sceneManager: Substitute.For<ISceneManager>(), database: new AssetDatabase());
        inspector = new NodeInspectorController(assets);
        undo = new UndoService(sceneTree, inspector, assets, loader);

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

    /// <summary>Editing ten mixed lights creates one undo step and restores every original intensity.</summary>
    [Fact]
    public void MultiObjectLightEditIsOneUndoStep()
    {
        var nodes = Enumerable.Range(0, 10).Select(index =>
        {
            var node = AddChild($"Light {index}");
            node.AddComponent(new LightComponent { Intensity = index + 1 });
            return node;
        }).ToArray();
        inspector.SelectMany(nodes, nodes[3]);
        var field = InspectorForms.BuildForNodes(nodes, inspector.CreateMutationNotifier()).Sections[1].Fields
            .Single(field => field.Name == nameof(LightComponent.Intensity));
        Assert.True(field.HasMixedValue);
        Assert.True(field.SetValue(4f));
        EndFrame();
        Assert.Single(undo.History.UndoSteps);
        Assert.All(nodes, node => Assert.Equal(4f, node.GetComponent<LightComponent>()!.Intensity));
        undo.Undo();
        for (var i = 0; i < nodes.Length; i++) Assert.Equal(i + 1, nodes[i].GetComponent<LightComponent>()!.Intensity);
        Assert.Equal(nodes, inspector.SelectedNodes);
        Assert.Same(nodes[3], inspector.SelectedNode);
        undo.Redo();
        Assert.All(nodes, node => Assert.Equal(4f, node.GetComponent<LightComponent>()!.Intensity));
    }

    /// <summary>Batch deletion covers selected descendants once and brings all subtrees back in one undo.</summary>
    [Fact]
    public void MultiDeleteAndDuplicateAreSingleSteps()
    {
        var first = AddChild("First");
        var second = AddChild("Second");
        var child = new Node { Name = "Child", Parent = first };
        first.Children.Add(child);
        inspector.SelectMany([first, child, second]);
        var operations = new NodeSelectionOperations(sceneTree, inspector, undo);
        operations.Delete(inspector.SelectedNodes);
        Assert.Empty(root.Children);
        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.Equal([first, second], root.Children);
        Assert.Same(first, child.Parent);
        Assert.Contains(child, first.Children);
        var copies = operations.Duplicate([first, child, second]);
        Assert.Equal(2, copies.Count);
        Assert.NotEqual(first.Id, copies[0].Id);
        Assert.Single(copies[0].Children);
        Assert.Equal(4, root.Children.Count);
        undo.Undo();
        Assert.Equal([first, second], root.Children);
        undo.Redo();
        Assert.Equal(4, root.Children.Count);
    }

    /// <summary>Batch reparenting preserves world placement and undo restores hierarchy and local transforms.</summary>
    [Fact]
    public void MultiReparentPreservesWorldAndRejectsCycles()
    {
        var first = AddChild("First");
        var second = AddChild("Second");
        var target = AddChild("Target");
        first.Position = new Vector3(1, 2, 3);
        second.Position = new Vector3(4, 5, 6);
        target.Position = new Vector3(10, 0, 0);
        var operations = new NodeSelectionOperations(sceneTree, inspector, undo);
        operations.Reparent([first, second], target);
        Assert.Equal(new Vector3(1, 2, 3), first.GlobalTransform.Position);
        Assert.Equal(new Vector3(4, 5, 6), second.GlobalTransform.Position);
        Assert.Single(undo.History.UndoSteps);
        operations.Reparent([target], first);
        Assert.Same(root, target.Parent);
        undo.Undo();
        Assert.Same(root, first.Parent);
        Assert.Same(root, second.Parent);
        Assert.Equal(new Vector3(1, 2, 3), first.Position);
        undo.Redo();
        Assert.Same(target, first.Parent);
        Assert.Same(target, second.Parent);
    }

    /// <summary>Batch duplicates have independent components and map references to copied peers and original externals.</summary>
    [Fact]
    public void MultiDuplicateRemapsPeerReferencesAndKeepsExternalReferences()
    {
        var first = AddChild("First");
        var second = AddChild("Second");
        var external = AddChild("External");
        var linker = new ObjectReferencesTests.Linker { Target = second, Waypoints = [second, external, first] };
        first.AddComponent(linker);
        var copies = new NodeSelectionOperations(sceneTree, inspector, undo).Duplicate([first, second]);
        var copiedLink = copies[0].GetComponent<ObjectReferencesTests.Linker>()!;
        Assert.NotSame(linker, copiedLink);
        Assert.NotEqual(linker.Id, copiedLink.Id);
        Assert.Same(copies[1], copiedLink.Target);
        Assert.Equal([copies[1], external, copies[0]], copiedLink.Waypoints!);
        Assert.Same(second, linker.Target);
        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.Equal([first, second, external], root.Children);
        undo.Redo();
        Assert.Contains(copies[0], root.Children);
        Assert.Contains(copies[1], root.Children);
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

        node.Position = new Vector3(4f, 0f, 0f);
        state.Restore(node);

        Assert.Equal(Vector3.Zero, node.Transform.Position);
    }

    sealed class LinkComponent : Component
    {
        public Component? Single { get; set; }
        public Component? Missing { get; set; }
        public Component[] Many { get; set; } = [];
        public List<Component> Listed { get; set; } = [];
        public int Count { get; set; }
    }

    /// <summary>A remapped snapshot swaps every referenced object, in members, arrays and lists, for its replacement.</summary>
    [Fact]
    public void ObjectState_RemapsReferencesOntoReplacements()
    {
        var (oldA, oldB, newA, newB) = (new LightComponent(), new LightComponent(), new LightComponent(), new LightComponent());
        var source = new LinkComponent { Single = oldA, Many = [oldA, oldB], Listed = [oldB], Count = 3 };
        var replacements = new Dictionary<IdObject, IdObject>(ReferenceEqualityComparer.Instance)
        {
            [oldA] = newA,
            [oldB] = newB
        };
        var target = new LinkComponent();

        ObjectState.Capture(source).Remap(typeof(LinkComponent), obj => replacements[obj]).Restore(target);

        Assert.Same(newA, target.Single);
        Assert.Null(target.Missing);
        Assert.Equal([newA, newB], target.Many);
        Assert.Equal([newB], target.Listed);
        Assert.Equal(3, target.Count);
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
        UndoStep Step(string label, DateTime at) => new(label, scene.Id,
            new Dictionary<IdObject, ObjectState>(ReferenceEqualityComparer.Instance) { [node] = ObjectState.Capture(node) },
            new Dictionary<IdObject, ObjectState>(ReferenceEqualityComparer.Instance) { [node] = ObjectState.Capture(node) })
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

    /// <summary>An inspected data asset's edits are steps of that asset, undone even after it is deselected.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiAssetEditsRestoreAndNotifyEveryPayload(bool locked)
    {
        var first = new ObjectReferencesTests.Stats { Health = 10 };
        var second = new ObjectReferencesTests.Stats { Health = 20 };
        var inspections = new[] { first, second }.Select((target, index) => new AssetInspection(
            new DataAssetAsset { Id = target.Id, RelativePath = $"Assets/stats{index}.dataasset" },
            $"/tmp/stats{index}.dataasset", target, "Stats", IsPayload: true)).ToArray();
        inspector.SelectMany(inspections);
        if (locked)
        {
            inspector.Select(root);
            undo.RecordInspections(inspections, "Edit Selection");
        }
        var field = InspectorForms.BuildForObjects([first, second], _ => undo.MarkAltered()).Sections[0].Fields
            .Single(member => member.Name == nameof(ObjectReferencesTests.Stats.Health));
        Assert.True(field.HasMixedValue);
        field.SetValue(50);
        undo.Flush();
        Assert.Single(undo.History.UndoSteps);
        var restored = new List<AssetInspection>();
        undo.AssetRestored += restored.Add;
        inspector.ClearSelection();
        undo.Undo();
        Assert.Equal(10, first.Health);
        Assert.Equal(20, second.Health);
        Assert.Equal(inspections.ToHashSet(), [.. restored]);
        restored.Clear();
        undo.Redo();
        Assert.Equal(50, first.Health);
        Assert.Equal(50, second.Health);
        Assert.Equal(inspections.ToHashSet(), [.. restored]);
    }

    /// <summary>An inspected data asset's edits are steps of that asset, undone even after it is deselected.</summary>
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

        Assert.Equal(metadata.Id, undo.History.UndoSteps[^1].Document);

        inspector.ClearSelection();
        undo.Undo();
        Assert.Equal(10, stats.Health);
    }

    /// <summary>Settings tag, layer name and list order edits undo and redo with slot identities intact.</summary>
    [Fact]
    public void UndoRestoresTagsLayersAndSettingsOrder()
    {
        var settings = new TagsAndLayersSettings();
        var physicsId = settings.PhysicsLayers[7].Id;
        var renderId = settings.RenderLayers[9].Id;
        var metadata = new DataAssetAsset { Id = Guid.NewGuid(), RelativePath = "Assets/layers.dataasset" };
        inspector.Select(new AssetInspection(metadata, "/tmp/layers.dataasset", settings, "Layers", IsPayload: true));
        settings.Tags.Add("Player");
        settings.PhysicsLayers[7].Name = "Characters";
        settings.RenderLayers[9].Name = "Effects";
        settings.PhysicsLayers.Reverse();
        settings.RenderLayers.Reverse();
        undo.MarkAltered();
        undo.Flush();
        undo.Undo();
        Assert.Equal(["Untagged"], settings.Tags);
        Assert.Equal(0, settings.PhysicsLayers[0].Index);
        Assert.Equal("Layer 7", settings.FindPhysicsLayer(7)!.Name);
        Assert.Equal(physicsId, settings.FindPhysicsLayer(7)!.Id);
        undo.Redo();
        Assert.Contains("Player", settings.Tags);
        Assert.Equal(31, settings.PhysicsLayers[0].Index);
        Assert.Equal("Characters", settings.FindPhysicsLayer(7)!.Name);
        Assert.Equal("Effects", settings.FindRenderLayer(9)!.Name);
        Assert.Equal(renderId, settings.FindRenderLayer(9)!.Id);
    }

    /// <summary>Undoing node tag edits refreshes the scene registry while keeping masks and indices.</summary>
    [Fact]
    public void UndoRefreshesNodeTagRegistry()
    {
        var node = AddChild("Tagged");
        root.Awake(null);
        inspector.Select(node);
        node.Tags = ["Player"];
        node.PhysicsLayer = 7;
        node.RenderLayer = 9;
        EndFrame();
        Assert.Same(node, root.FindWithTag("Player"));
        undo.Undo();
        Assert.Null(root.FindWithTag("Player"));
        Assert.Equal(0, node.PhysicsLayer);
        undo.Redo();
        Assert.Same(node, root.FindWithTag("Player"));
        Assert.Equal(7, node.PhysicsLayer);
        Assert.Equal(9, node.RenderLayer);
    }

    /// <summary>Undo reaches back into another scene and brings it to the front.</summary>
    [Fact]
    public void Undo_SwitchesToTheStepsScene()
    {
        var box = AddChild("Box");
        inspector.Select(box);
        box.Name = "Crate";
        EndFrame();

        var other = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/other.prefab" };
        assets.OpenAsset(other);
        sceneTree.OpenAsset(other);

        Asset? activated = null;
        assets.AssetOpened += asset => activated = asset;

        undo.Undo();

        Assert.Equal("Box", box.Name);
        Assert.Equal(scene.Id, activated?.Id);
    }

    /// <summary>A rebuilt scene keeps its history: the steps move to the rebuilt objects by id.</summary>
    [Fact]
    public void Undo_SurvivesASceneRebuild()
    {
        var box = AddChild("Box");
        inspector.Select(box);
        box.Name = "Crate";
        EndFrame();

        var rebuilt = NodeCloner.DeepClone(root)!;
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, rebuilt);
        RaiseRebuilt(root, rebuilt);

        undo.Undo();

        Assert.Equal("Box", rebuilt.Children[0].Name);
        Assert.Equal("Crate", box.Name);
    }

    void RaiseRebuilt(Node oldRoot, Node newRoot)
    {
        var field = typeof(SceneTreeController).GetField(nameof(SceneTreeController.SceneRebuilt),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Action<Guid, Node, Node>?)field.GetValue(sceneTree))?.Invoke(scene.Id, oldRoot, newRoot);
    }

    /// <summary>A change beyond scene objects is undone by its effect, and the kept objects get their values back.</summary>
    [Fact]
    public void Perform_UndoesItsEffectAndKeepsValues()
    {
        var box = AddChild("Box");
        var file = "old";

        undo.Perform("Apply to Prefab", [box], () => { file = "new"; box.Name = "Changed by rebuild"; },
            () => { file = "old"; box.Name = "Changed by rebuild"; }, keepValues: true);
        Assert.Equal("new", file);

        undo.Undo();
        Assert.Equal("old", file);
        Assert.Equal("Box", box.Name);

        undo.Redo();
        Assert.Equal("new", file);
        Assert.Equal("Box", box.Name);
    }

    /// <summary>Undoing back to the saved step makes the scene clean again; undoing further dirties it.</summary>
    [Fact]
    public void Undo_ToTheSavedStep_ClearsTheDirtyMarker()
    {
        var box = AddChild("Box");
        inspector.Select(box);
        box.Name = "Saved";
        EndFrame();
        assets.SaveAsset(scene);

        box.Name = "Edited";
        EndFrame();
        Assert.True(scene.IsModified);

        undo.Undo();
        Assert.Equal("Saved", box.Name);
        Assert.False(scene.IsModified);

        undo.Undo();
        Assert.True(scene.IsModified);
    }

    /// <summary>A gesture is one step, whatever it changes and however long it lasts.</summary>
    [Fact]
    public void Gesture_IsOneStep()
    {
        var box = AddChild("Box");
        box.AddComponent(new LightComponent());
        inspector.Select(box);

        undo.BeginGesture();
        box.Position = new Vector3(1f, 0f, 0f);
        EndFrame();
        box.GetComponent<LightComponent>()!.Intensity = 3f;
        EndFrame();
        undo.RecordObject(root, "Other");
        box.Position = new Vector3(2f, 0f, 0f);
        EndFrame();
        undo.EndGesture();

        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.Equal(Vector3.Zero, box.Transform.Position);
        Assert.Equal(1f, box.GetComponent<LightComponent>()!.Intensity);
    }

    /// <summary>A change that throws records nothing and keeps what could be redone.</summary>
    [Fact]
    public void Perform_ThatThrows_RecordsNothing()
    {
        var box = AddChild("Box");
        inspector.Select(box);
        box.Name = "Crate";
        EndFrame();
        undo.Undo();

        Assert.Throws<InvalidOperationException>(() => undo.Perform("Apply to Prefab", [box],
            () => throw new InvalidOperationException(), () => { }, keepValues: true));

        Assert.False(undo.CanUndo);
        Assert.True(undo.CanRedo);
    }

    /// <summary>A node deleted before a rebuild comes back as a rebuilt copy with the same id.</summary>
    [Fact]
    public void Undo_OfDeleteAfterRebuild_RestoresTheNode()
    {
        var box = AddChild("Box");
        undo.RecordObject(root, "Delete");
        sceneTree.DetachNode(box);
        EndFrame();

        var rebuilt = NodeCloner.DeepClone(root)!;
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, rebuilt);
        RaiseRebuilt(root, rebuilt);

        undo.Undo();

        var restored = Assert.Single(rebuilt.Children);
        Assert.Equal(box.Id, restored.Id);
        Assert.NotSame(box, restored);
        Assert.Same(rebuilt, restored.Parent);
    }
}
