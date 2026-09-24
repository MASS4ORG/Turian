namespace Turian.Tests.Editor;

/// <summary>
/// Covers the set of assets open for editing: what a shell draws as tabs, and the policy questions —
/// is anything unsaved, can play mode start — that belong in the core rather than in a shell.
/// </summary>
public class AssetWorkspaceTests : IDisposable
{
    readonly AssetManager assets = new();
    readonly SettingsService settings = new();
    readonly AssetWorkspace workspace;

    /// <summary>Creates a workspace over a fresh asset manager.</summary>
    public AssetWorkspaceTests() => workspace = new AssetWorkspace(assets, settings);

    /// <summary>Releases the workspace's subscriptions.</summary>
    public void Dispose()
    {
        workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    static Prefab Scene(string name) =>
        new() { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}.prefab" };

    static MaterialAsset Material(string name) =>
        new() { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}.mat" };

    /// <summary>A newly created workspace has nothing open.</summary>
    [Fact]
    public void AFreshWorkspaceIsEmpty()
    {
        Assert.Empty(workspace.Documents);
        Assert.Null(workspace.Active);
        Assert.False(workspace.HasOpenScene);
        Assert.False(workspace.HasUnsavedChanges);
    }

    /// <summary>Several assets stay open at once, and the last opened is active.</summary>
    [Fact]
    public void SeveralAssetsStayOpenInParallel()
    {
        var first = workspace.Open(Scene("one"));
        var second = workspace.Open(Scene("two"));
        var third = workspace.Open(Material("brick"));

        Assert.Equal([first, second, third], workspace.Documents);
        Assert.Same(third, workspace.Active);
    }

    /// <summary>Opening the same asset twice activates the existing document rather than duplicating it.</summary>
    [Fact]
    public void ReopeningAnAssetActivatesTheDocumentAlreadyOpen()
    {
        var scene = Scene("one");
        var document = workspace.Open(scene);
        workspace.Open(Scene("two"));

        var reopened = workspace.Open(scene);

        Assert.Same(document, reopened);
        Assert.Equal(2, workspace.Documents.Count);
        Assert.Same(document, workspace.Active);
    }

    /// <summary>Activating a document raises the event a tab strip listens to.</summary>
    [Fact]
    public void ActivatingRaisesTheEventAndMovesTheActiveDocument()
    {
        var first = workspace.Open(Scene("one"));
        workspace.Open(Scene("two"));

        AssetWrapper? activated = null;
        workspace.Activated += document => activated = document;

        workspace.Activate(first);

        Assert.Same(first, workspace.Active);
        Assert.Same(first, activated);
    }

    /// <summary>Play mode needs a scene; a material alone is not enough.</summary>
    [Fact]
    public void OnlyASceneMakesPlayModeAvailable()
    {
        workspace.Open(Material("brick"));
        Assert.False(workspace.HasOpenScene);

        workspace.Open(Scene("one"));
        Assert.True(workspace.HasOpenScene);
    }

    /// <summary>An edited asset reports unsaved changes and wants a prompt before closing.</summary>
    [Fact]
    public void AnEditedDocumentIsDirtyAndAsksBeforeClosing()
    {
        var scene = Scene("one");
        var document = workspace.Open(scene);
        Assert.False(AssetWorkspace.NeedsSavePrompt(document));

        scene.MarkModified();

        Assert.True(document.IsDirty);
        Assert.True(workspace.HasUnsavedChanges);
        Assert.True(AssetWorkspace.NeedsSavePrompt(document));
    }

    /// <summary>Closing with Save writes the asset out first, so nothing is left dirty.</summary>
    [Fact]
    public void ClosingWithSaveClearsTheDirtyState()
    {
        var scene = Scene("one");
        var document = workspace.Open(scene);
        scene.MarkModified();

        workspace.Close(document, UnsavedChanges.Save);

        Assert.Empty(workspace.Documents);
        Assert.False(scene.IsModified);
    }

    /// <summary>Closing the active document falls back to another open one.</summary>
    [Fact]
    public void ClosingTheActiveDocumentActivatesAnother()
    {
        var first = workspace.Open(Scene("one"));
        var second = workspace.Open(Scene("two"));

        workspace.Close(second);

        Assert.Equal([first], workspace.Documents);
        Assert.Same(first, workspace.Active);
    }

    /// <summary>Closing the last document leaves nothing active.</summary>
    [Fact]
    public void ClosingTheLastDocumentLeavesNothingActive()
    {
        var only = workspace.Open(Scene("one"));

        workspace.Close(only);

        Assert.Empty(workspace.Documents);
        Assert.Null(workspace.Active);
    }

    /// <summary>Loading a project clears the previous project's documents.</summary>
    [Fact]
    public void OpeningAProjectClearsTheDesk()
    {
        workspace.Open(Scene("one"));
        workspace.Open(Scene("two"));

        settings.Set(new AppSettings());

        Assert.Empty(workspace.Documents);
        Assert.Null(workspace.Active);
    }

    /// <summary>The captured session names the open assets and the active one.</summary>
    [Fact]
    public void CaptureRecordsWhatToReopenNextTime()
    {
        var first = Scene("one");
        var second = Scene("two");
        workspace.Open(first);
        workspace.Open(second);

        var session = workspace.Capture();

        Assert.Equal([first.Id, second.Id], session.OpenAssetIds);
        Assert.Equal(second.Id, session.ActiveAssetId);
    }

    /// <summary>Closing the open scene clears the scene tree, instead of leaving a closed hierarchy on screen.</summary>
    [Fact]
    public void ClosingTheOpenSceneEmptiesTheSceneTree()
    {
        var sceneTree = new SceneTreeController(assets, settings, null!);
        var nodeInspector = new NodeInspectorController(assets);
        using var binder = new SceneDocumentBinder(assets, sceneTree, nodeInspector);
        binder.Attach();

        var scene = Scene("one");
        var document = workspace.Open(scene);
        Assert.Equal(scene.Id, sceneTree.CurrentAsset?.Id);

        workspace.Close(document);

        Assert.Null(sceneTree.CurrentAsset);
        Assert.Null(sceneTree.CurrentSceneRoot);
    }
}
