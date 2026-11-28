namespace Turian.Tests;

/// <summary>Edits to a read-only scene are taken back as they happen, and nothing is written for it.</summary>
public sealed class ReadOnlySceneEditTests : IDisposable
{
    readonly AssetManager assets = new();
    readonly NodeInspectorController inspector;
    readonly SceneTreeController sceneTree;
    readonly UndoService undo;
    readonly Prefab scene = new() { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
    readonly Node root = new() { Name = "Root" };
    readonly Node child = new() { Name = "Child" };

    /// <summary>Opens a read-only scene with no file behind it; the importer is never reached.</summary>
    public ReadOnlySceneEditTests()
    {
        var loader = Substitute.For<IAssetLoader>();
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!,
            assetLoader: loader, sceneManager: Substitute.For<ISceneManager>(), database: new AssetDatabase());
        inspector = new NodeInspectorController(assets);
        undo = new UndoService(sceneTree, inspector, assets, loader);
        assets.SetReadOnly(scene.Id);
        assets.OpenAsset(scene);
        sceneTree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
        root.Children.Add(child);
        child.Parent = root;
    }

    /// <inheritdoc />
    public void Dispose() => undo.Dispose();

    void AssertUntouched()
    {
        Assert.Empty(undo.History.UndoSteps);
        Assert.False(scene.IsModified);
        Assert.False(undo.CanUndo);
    }

    /// <summary>An Inspector edit of a selected node goes back at the end of the frame.</summary>
    [Fact]
    public void AnInspectorEditIsTakenBack()
    {
        inspector.Select(child);
        var field = InspectorForms.BuildForNodes([child], inspector.CreateMutationNotifier()).Sections[0].Fields
            .Single(static field => field.Name == nameof(Node.Name));

        Assert.True(field.SetValue("Renamed"));
        undo.Flush();

        Assert.Equal("Child", child.Name);
        Assert.Same(child, inspector.SelectedNode);
        AssertUntouched();
    }

    /// <summary>Deleting nodes brings them back.</summary>
    [Fact]
    public void ADeletedNodeComesBack()
    {
        inspector.Select(child);

        new NodeSelectionOperations(sceneTree, inspector, undo).Delete(inspector.SelectedNodes);
        undo.Flush();

        Assert.Equal([child], root.Children);
        Assert.Same(root, child.Parent);
        AssertUntouched();
    }

    /// <summary>A change that reaches beyond the scene, such as saving a node as a prefab, does not run.</summary>
    [Fact]
    public void ChangesBeyondTheSceneDoNotRun()
    {
        var ran = false;

        Assert.False(undo.Perform("Write", [child], () => ran = true, () => { }));
        var prefab = new PrefabAuthoring(null!, null!, undo, new AssetDatabase())
            .CreatePrefab(child, Path.GetTempPath());

        Assert.False(ran);
        Assert.Null(prefab);
        AssertUntouched();
    }

    /// <summary>A change that belongs to the whole project, such as moving files, is still made and recorded.</summary>
    [Fact]
    public void ProjectChangesStillRun()
    {
        var ran = false;

        Assert.True(undo.Perform("Move", [], () => ran = true, () => { }, document: UndoService.ProjectDocument));

        Assert.True(ran);
        Assert.Single(undo.History.UndoSteps);
    }
}
