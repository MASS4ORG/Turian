namespace Turian.Tests;

/// <summary>Checks that workspace tabs become Gaya documents, each with its own selection, play binding and camera.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneDocumentBridgeTests
{
    /// <summary>Every tab is a scene document keyed by the id the tab strip uses, and activation follows the tabs.</summary>
    [Fact]
    public void TabsBecomeDocumentsKeyedByTheTabStripId()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        var untitled = harness.Workspace.NewScene();

        var ids = harness.Workspace.Documents.Select(static tab => tab.Asset!.Id.ToString()).ToList();
        Assert.Equal(ids, harness.Documents.Documents.Select(static document => document.Id));
        Assert.All(harness.Documents.Documents, static document => Assert.Equal("turian.scene", document.Kind));
        Assert.Equal("Untitled 1", harness.Documents.Active!.Title);
        Assert.Equal(untitled.Asset!.Id.ToString(), harness.Documents.Active.Id);

        harness.Activate(one);
        Assert.Same(one, harness.Documents.Active);
        harness.Activate(two);
        Assert.Same(two, harness.Documents.Active);
    }

    /// <summary>Scenes, prefabs and temporary scenes are scene documents; any other asset is an asset document.</summary>
    [Fact]
    public void KindFollowsTheAssetType()
    {
        using var harness = new SceneTabsHarness();
        harness.Open("one");
        var material = harness.OpenMaterial("brick");
        var untitled = harness.Workspace.NewScene();

        Assert.Equal("turian.asset", material.Kind);
        Assert.Equal("brick", material.Title);
        Assert.Equal(["turian.scene", "turian.asset", "turian.scene"],
            harness.Documents.Documents.Select(static document => document.Kind));
        Assert.Equal(untitled.Asset!.Id.ToString(), harness.Documents.Active!.Id);
        Assert.Throws<ArgumentException>(static () => new SceneDocument(new AssetWrapper(null)));
        Assert.Throws<ArgumentException>(static () => new AssetDocument(new AssetWrapper(null)));
    }

    /// <summary>A workspace with no tabs publishes no document and leaves none active.</summary>
    [Fact]
    public void EmptyWorkspacePublishesNoDocument()
    {
        var workspace = Substitute.For<IDocumentService>();
        var assets = new AssetManager();
        using var real = new AssetWorkspace(assets, new SettingsService());
        using var bridge = new DocumentWorkspaceBridge(real, workspace);

        workspace.Received().Deactivate();
        workspace.DidNotReceive().Open(Arg.Any<IDocument>());
    }

    /// <summary>Selecting an asset tab clears the scene's node selection, and the scene tab brings its own back.</summary>
    [Fact]
    public void AssetTabHidesSceneSelectionUntilTheSceneIsBack()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a");
        harness.Inspector.Select(harness.Node(one, "a"));

        harness.OpenMaterial("brick");
        Assert.Empty(harness.Inspector.SelectedNodes);

        harness.Activate(one);
        Assert.Same(harness.Node(one, "a"), harness.Inspector.SelectedNode);
    }

    /// <summary>A document's title follows the tab as the asset is renamed.</summary>
    [Fact]
    public void TitleFollowsTheTab()
    {
        using var harness = new SceneTabsHarness();
        var tab = harness.Open("before");

        tab.Wrapper.Asset!.RenameTo("Assets/after.prefab");

        Assert.Equal("after", tab.Title);
    }

    /// <summary>Closing the active tab leaves the last remaining one active, and the last close leaves no document.</summary>
    [Fact]
    public void ClosingTabsLeavesTheRemainingOneActiveThenNone()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");

        harness.Workspace.Close(two.Wrapper);
        Assert.Same(one, harness.Documents.Active);
        Assert.Equal([one.Id], harness.Documents.Documents.Select(static document => document.Id));

        harness.Workspace.Close(one.Wrapper);
        Assert.Null(harness.Documents.Active);
        Assert.Empty(harness.Documents.Documents);
    }

    /// <summary>Opening another project closes every document, which leaves the no-document state.</summary>
    [Fact]
    public void ProjectSwitchClosesEveryDocument()
    {
        using var harness = new SceneTabsHarness();
        harness.Open("one");
        harness.Open("two");
        var closed = new List<string>();
        harness.Documents.Closed += document => closed.Add(document.Title);

        harness.Settings.Set(new AppSettings());

        Assert.Equal(["one", "two"], closed.Order());
        Assert.Empty(harness.Documents.Documents);
        Assert.Null(harness.Documents.Active);
    }

    /// <summary>Tabs that were already open when the bridge started are adopted, with the active one active.</summary>
    [Fact]
    public void TabsOpenBeforeTheBridgeAreAdopted()
    {
        var assets = new AssetManager();
        using var workspace = new AssetWorkspace(assets, new SettingsService());
        workspace.Open(new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/one.prefab" });
        var two = workspace.Open(new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/two.prefab" });
        var documents = new DocumentService();

        using var bridge = new DocumentWorkspaceBridge(workspace, documents);

        Assert.Equal(2, documents.Documents.Count);
        Assert.Equal(two.Asset!.Id.ToString(), documents.Active!.Id);
    }

    /// <summary>The Inspector shows the active tab's selection, and switching back restores each tab's own.</summary>
    [Fact]
    public void SelectionBelongsToTheActiveTab()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a", "b");
        harness.Inspector.Select(harness.Node(one, "a", "b"));
        var two = harness.Open("two", "x");

        Assert.Empty(harness.Inspector.SelectedNodes);
        harness.Inspector.SelectMany([harness.Root(two), harness.Node(two, "x")], harness.Node(two, "x"));

        harness.Activate(one);
        Assert.Equal([harness.Node(one, "a", "b")], harness.Inspector.SelectedNodes);
        Assert.Same(harness.Node(one, "a", "b"), harness.Inspector.SelectedNode);

        harness.Activate(two);
        Assert.Equal([harness.Root(two), harness.Node(two, "x")], harness.Inspector.SelectedNodes);
        Assert.Same(harness.Node(two, "x"), harness.Inspector.SelectedNode);
    }

    /// <summary>A closed tab's remembered selection is dropped, and the tab that takes over shows its own.</summary>
    [Fact]
    public void ClosingTheActiveTabShowsTheNextOnesSelection()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one", "a");
        harness.Inspector.Select(harness.Node(one, "a"));
        var two = harness.Open("two", "x");
        harness.Inspector.Select(harness.Node(two, "x"));

        harness.Workspace.Close(two.Wrapper);

        Assert.Same(harness.Node(one, "a"), harness.Inspector.SelectedNode);
    }

    /// <summary>A selection that is not a node, such as an asset, survives opening a tab with nothing remembered.</summary>
    [Fact]
    public void OpeningATabKeepsANonNodeSelection()
    {
        using var harness = new SceneTabsHarness();
        var asset = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/other.prefab" };
        harness.Inspector.Select(asset);

        harness.Open("one");

        Assert.Same(asset, harness.Inspector.SelectedObject);
    }

    /// <summary>The game belongs to the tab that started it, through tab switches, until that tab closes.</summary>
    [Fact]
    public void PlayBindsToTheStartingTab()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        Assert.False(harness.Playing.IsPlayingElsewhere(one.Id));

        harness.Activate(one);
        Assert.True(harness.Play.Start());
        Assert.True(harness.Playing.IsPlayingIn(one.Id));
        Assert.True(harness.Playing.IsPlayingElsewhere(two.Id));
        Assert.Equal("one", harness.Playing.Title);

        harness.Activate(two);
        Assert.True(harness.Play.IsActive);
        Assert.True(harness.Playing.IsPlayingIn(one.Id));
        Assert.True(harness.Playing.IsPlayingElsewhere(two.Id));

        harness.Workspace.Close(one.Wrapper);
        Assert.True(harness.Play.IsActive);
        Assert.True(harness.Playing.IsPlayingIn(two.Id));
        Assert.False(harness.Playing.IsPlayingElsewhere(two.Id));

        harness.Play.Stop();
        Assert.Null(harness.Playing.Title);
        Assert.False(harness.Playing.IsPlayingElsewhere(one.Id));
    }

    /// <summary>Leaving the playing tab releases the keys the game holds, so none stays down while its panel is hidden.</summary>
    [Fact]
    public void LeavingThePlayingTabReleasesHeldKeys()
    {
        using var harness = new SceneTabsHarness();
        var one = harness.Open("one");
        var two = harness.Open("two");
        harness.Activate(one);
        Assert.True(harness.Play.Start());
        harness.Play.Input.PushKeyDown(Key.W);
        Assert.True(harness.Play.Input.IsKeyDown(Key.W));

        harness.Activate(two);

        Assert.False(harness.Play.Input.IsKeyDown(Key.W));
    }

    /// <summary>A camera pose carries position, orientation, projection and orbit state to another camera.</summary>
    [Fact]
    public void CameraPoseRoundTrips()
    {
        var camera = new EditorCamera { Position = new Vector3(1, 2, 3), IsOrthographic = true, Frustum = 7 };
        camera.SetYawPitch(1f, 0.3f);
        var controller = new SceneCameraController(camera) { OrbitPivot = new Vector3(4, 5, 6), OrbitDistance = 9 };
        var pose = ViewportCameraPose.Capture(controller);

        var restored = new SceneCameraController(new EditorCamera());
        pose.Apply(restored);

        Assert.True(Vector3.Distance(camera.Position, restored.Camera.Position) < 1e-5f);
        Assert.True(Quaternion.Dot(camera.Orientation, restored.Camera.Orientation) > 0.99999f);
        Assert.True(restored.Camera.IsOrthographic);
        Assert.Equal(7f, restored.Camera.Frustum, 1e-5f);
        Assert.True(Vector3.Distance(controller.OrbitPivot, restored.OrbitPivot) < 1e-5f);
        Assert.Equal(9f, restored.OrbitDistance, 1e-5f);
    }
}
