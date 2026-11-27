namespace Turian.Tests.Editor;

/// <summary>
/// The asset manager reports a scene as saved even when the scene writer could not write it and marked it modified
/// again; nothing that closes or leaves may trust that report.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class SceneSaveVerificationTests : IDisposable
{
    readonly AssetManager assets = new();
    readonly SettingsService settings = new();
    readonly AssetDatabase database = new();
    readonly AssetWorkspace workspace;
    readonly SceneTreeController tree;
    readonly SceneDocumentBinder binder;
    readonly AssetWrapper scene;
    readonly string directory;

    /// <summary>Opens a scene with an edit whose file cannot be written, as the real scene writer sees it.</summary>
    public SceneSaveVerificationTests()
    {
        workspace = new AssetWorkspace(assets, settings);
        var loader = new RuntimeAssetLoader(database);
        var manager = new SceneManager(database);
        manager.BindAssetLoader(loader);
        tree = new SceneTreeController(assets, settings, null!, loader, manager, database);
        binder = new SceneDocumentBinder(assets, tree, new NodeInspectorController(assets));
        binder.Attach();

        scene = workspace.NewScene();
        directory = Path.GetDirectoryName(scene.Asset!.RelativePath)!;

        // A folder where the file should be makes every write fail.
        scene.Asset.RelativePath = directory;
        tree.CurrentSceneRoot!.Children.Add(new Node { Name = "Unsaved", Parent = tree.CurrentSceneRoot });
        tree.MarkAssetModified();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        workspace.Dispose();
        binder.Dispose();
    }

    /// <summary>The premise: the manager says the scene was saved while it is still modified.</summary>
    [Fact]
    public void TheManagerReportsSuccessForASceneThatIsStillModified()
    {
        Assert.True(assets.SaveAsset(scene.Asset!));
        Assert.True(scene.Asset!.IsModified);
    }

    /// <summary>Closing with Save keeps the scene and its edits when the write did not happen.</summary>
    [Fact]
    public void CloseWithSaveKeepsASceneThatIsStillModified()
    {
        Assert.False(workspace.Close(scene, UnsavedChanges.Save));

        Assert.Same(scene, Assert.Single(workspace.Documents));
        Assert.True(scene.IsDirty);
        Assert.Equal("Unsaved", Assert.Single(tree.CurrentSceneRoot!.Children).Name);
    }

    /// <summary>Save All says a scene that is still modified was not saved.</summary>
    [Fact]
    public void SaveAllFailsForASceneThatIsStillModified()
    {
        Assert.False(workspace.SaveAll());
        Assert.True(scene.IsDirty);
    }

    /// <summary>Leaving the project is blocked while the scene could not be written.</summary>
    [Fact]
    public void LeavingTheProjectIsBlockedByASceneThatIsStillModified()
    {
        var unsaved = new UnsavedWork(workspace, new AssetAutoSave(new AssetInspectionService(null!,
            new AssetTypeCatalog(), settings, null!, NullLogger.Instance)));

        Assert.False(unsaved.SaveAll());
        Assert.True(unsaved.Any);
        Assert.Same(scene, Assert.Single(workspace.Documents));
    }

    /// <summary>Once the file can be written, the same calls succeed.</summary>
    [Fact]
    public void ASceneThatCanBeWrittenIsSavedAndClosed()
    {
        scene.Asset!.RelativePath = Path.Combine(directory, "writable.prefab");

        Assert.True(workspace.SaveAll());
        Assert.False(scene.IsDirty);
        tree.MarkAssetModified();
        Assert.True(workspace.Close(scene, UnsavedChanges.Save));
        Assert.Empty(workspace.Documents);
    }
}
