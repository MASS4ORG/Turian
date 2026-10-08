namespace Turian.Tests.Editor;

/// <summary>Checks temporary scene editing, storage, play isolation and document lifetime.</summary>
[Collection(SerialTests.Name)]
public sealed class TemporarySceneTests : IDisposable
{
    readonly AssetManager assets = new();
    readonly SettingsService settings = new();
    readonly AssetDatabase database = new();
    readonly AssetWorkspace workspace;
    readonly SceneTreeController tree;
    readonly NodeInspectorController inspector;
    readonly SceneDocumentBinder binder;
    readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();

    /// <summary>Connects the real scene loader to a workspace without project settings.</summary>
    public TemporarySceneTests()
    {
        workspace = new AssetWorkspace(assets, settings);
        inspector = new NodeInspectorController(assets);
        var loader = new RuntimeAssetLoader(database);
        var manager = new SceneManager(database);
        manager.BindAssetLoader(loader);
        tree = new SceneTreeController(assets, settings, null!, loader, manager, database);
        binder = new SceneDocumentBinder(assets, tree, inspector);
        binder.Attach();
    }

    /// <summary>Releases the workspace and all temporary scene files.</summary>
    public void Dispose()
    {
        workspace.Dispose();
        binder.Dispose();
        services.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>New scenes have independent identities and tabs, and never enter the saved asset session.</summary>
    [Fact]
    public void NewScenesOpenIndependentEmptyHierarchies()
    {
        var first = workspace.NewScene();
        var firstRoot = tree.CurrentSceneRoot;
        var second = workspace.NewScene();
        Assert.NotEqual(first.Asset!.Id, second.Asset!.Id);
        Assert.Equal("Untitled 1", first.Title);
        Assert.Equal("Untitled 2", second.Title);
        Assert.Same(second, workspace.Active);
        Assert.True(workspace.HasOpenScene);
        Assert.Empty(tree.CurrentSceneRoot!.Children);
        Assert.Equal(second.Title, tree.CurrentSceneRoot.Name);
        Assert.Empty(workspace.Capture().OpenAssetIds);
        Assert.Null(workspace.Capture().ActiveAssetId);
        Assert.False(database.TryGetAsset(first.Asset.Id, out _));
        workspace.Activate(first);
        Assert.Same(firstRoot, tree.CurrentSceneRoot);
    }

    /// <summary>Editing, undo, redo and save operate on the temporary prefab without needing an importer.</summary>
    [Fact]
    public async Task EditsCanBeUndoneAndSavedWithoutAProject()
    {
        var document = workspace.NewScene();
        var root = tree.CurrentSceneRoot!;
        using var undo = new UndoService(tree, inspector, assets, new RuntimeAssetLoader(database));
        undo.Perform("Add Node", [root], () =>
        {
            root.Children.Add(new Node { Name = "Test node", Parent = root });
            tree.MarkAssetModified();
        }, () => root.Children.Clear());
        Assert.True(document.IsDirty);
        undo.Undo();
        Assert.Empty(root.Children);
        undo.Redo();
        Assert.Single(root.Children);
        workspace.SaveAll();
        Assert.False(document.IsDirty);
        var saved = await new SceneManager(database).LoadNodeAsync(document.Asset!.RelativePath);
        Assert.Equal("Test node", Assert.Single(saved.Children).Name);
    }

    /// <summary>A failed write keeps temporary scene edits dirty so the user can retry saving.</summary>
    [Fact]
    public void FailedTemporarySaveRetainsTheDirtyState()
    {
        var document = workspace.NewScene();
        document.Asset!.RelativePath = Path.GetDirectoryName(document.Asset.RelativePath)!;
        tree.MarkAssetModified();
        workspace.Save(document);
        Assert.True(document.IsDirty);
    }

    /// <summary>Play mode runs a copy and restores the edited scene after stopping.</summary>
    [Fact]
    public void PlayModeUsesACopyOfTheTemporaryScene()
    {
        var document = workspace.NewScene();
        var root = tree.CurrentSceneRoot!;
        root.Children.Add(new Node { Name = "Test node", Parent = root });
        tree.MarkAssetModified();
        var play = new PlayModeService(tree, database, services, NullLogger.Instance);
        try
        {
            Assert.True(play.Start());
            Assert.NotSame(root, tree.CurrentSceneRoot);
            play.PlayRoot!.Children[0].Name = "Runtime edit";
            workspace.Save(document);
            var saved = Serializer.Load<Node>(document.Asset!.RelativePath)!;
            Assert.Equal("Test node", Assert.Single(saved.Children).Name);
            play.Pause();
            play.StepFrame();
            play.Stop();
            Assert.Same(root, tree.CurrentSceneRoot);
            Assert.Equal("Test node", Assert.Single(root.Children).Name);
        }
        finally
        {
            play.Stop();
        }
    }

    /// <summary>Project prefab instances retain their links and overrides when a temporary scene is saved.</summary>
    [Fact]
    public async Task ProjectPrefabInstancesRoundTripThroughTemporaryStorage()
    {
        var project = Directory.CreateTempSubdirectory("turian-scene-project-");
        try
        {
            var assetFolder = project.CreateSubdirectory("Assets");
            var path = Path.Combine(assetFolder.FullName, "Reusable.prefab");
            File.WriteAllText(path, Serializer.Serialize(new Node { Name = "Reusable" }));
            var prefab = new Prefab { RelativePath = path };
            Assert.True(database.RegisterAsset(prefab));

            var document = workspace.NewScene();
            var manager = new SceneManager(database);
            manager.BindAssetLoader(new RuntimeAssetLoader(database));
            var instance = await manager.InstantiateAsync(prefab.Id, tree.CurrentSceneRoot);
            instance.Name = "Overridden name";
            tree.MarkAssetModified();
            workspace.Save(document);
            Assert.False(document.IsDirty);
            var json = JsonNode.Parse(File.ReadAllText(document.Asset!.RelativePath))!;
            Assert.NotNull(json["Children"]![0]!["PrefabInstance"]);
            var loaded = await manager.LoadNodeAsync(document.Asset.RelativePath);
            var savedInstance = Assert.Single(loaded.Children);
            Assert.Equal("Overridden name", savedInstance.Name);
            Assert.Equal(prefab.Id, savedInstance.PrefabInstance!.Source.AssetId);
            using var stage = new PrefabStage(workspace, database);
            Assert.True(stage.OpenPrefab(instance));
            stage.Return(0);
            Assert.Same(document, workspace.Active);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    /// <summary>Closing a temporary document deletes its backing files and retains the other open scene.</summary>
    [Fact]
    public void ClosingDeletesOnlyThatScenesTemporaryFolder()
    {
        var first = workspace.NewScene();
        var second = workspace.NewScene();
        var folder = Path.GetDirectoryName(second.Asset!.RelativePath)!;
        tree.MarkAssetModified();
        Assert.True(AssetWorkspace.NeedsSavePrompt(second));
        workspace.Close(second, UnsavedChanges.Save);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(first.Asset!.RelativePath));
        Assert.Same(first, workspace.Active);
        Assert.Same(first.Asset, tree.CurrentAsset);
    }

    /// <summary>Project switches and disposal delete temporary files without recording them for restoration.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetDeletesTemporaryScenes(bool switchProject)
    {
        var document = workspace.NewScene();
        var folder = Path.GetDirectoryName(document.Asset!.RelativePath)!;
        if (switchProject) settings.Set(new AppSettings());
        else workspace.Dispose();
        Assert.False(Directory.Exists(folder));
        Assert.Empty(workspace.Documents);
        Assert.Null(tree.CurrentSceneRoot);
    }
}
