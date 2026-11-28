namespace Turian.Tests;

/// <summary>
/// The Studio's scene tabs without a window: a workspace over a real asset manager and scene tree, mirrored into Gaya
/// documents with per-tab selection and the play binding, as the plugin wires them. Scenes come from memory.
/// </summary>
sealed class SceneTabsHarness : IDisposable
{
    readonly Dictionary<Guid, Node> roots = [];
    readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();

    /// <summary>Wires the tabs over <paramref name="documents"/>, or a private document service.</summary>
    /// <param name="documents">The workbench's document service, when a workbench is part of the test.</param>
    public SceneTabsHarness(DocumentService? documents = null)
    {
        SceneManager.LoadNodeAsync(Arg.Any<Guid>()).Returns(call => Task.FromResult(roots[call.Arg<Guid>()]));
        Tree = new SceneTreeController(Assets, Settings, null!, Loader, SceneManager, Database);
        Inspector = new NodeInspectorController(Assets);
        Undo = new UndoService(Tree, Inspector, Assets, Loader);
        Workspace = new AssetWorkspace(Assets, Settings);
        Binder = new SceneDocumentBinder(Assets, Tree, Inspector);
        Binder.Attach();
        Documents = documents ?? new DocumentService();
        Play = new PlayModeService(Tree, Database, services, NullLogger.Instance);
        Playing = new PlayingDocument(Play, Documents);
        Memory = new DocumentSelectionMemory(Documents, Inspector, Tree);
        Bridge = new DocumentWorkspaceBridge(Workspace, Documents);
    }

    public AssetManager Assets { get; } = new();
    public SettingsService Settings { get; } = new();
    public AssetDatabase Database { get; } = new();
    public IAssetLoader Loader { get; } = Substitute.For<IAssetLoader>();
    public ISceneManager SceneManager { get; } = Substitute.For<ISceneManager>();
    public SceneTreeController Tree { get; }
    public NodeInspectorController Inspector { get; }
    public UndoService Undo { get; }
    public AssetWorkspace Workspace { get; }
    public SceneDocumentBinder Binder { get; }
    public DocumentService Documents { get; }
    public PlayModeService Play { get; }
    public PlayingDocument Playing { get; }
    public DocumentSelectionMemory Memory { get; }
    public DocumentWorkspaceBridge Bridge { get; }

    /// <summary>Opens a scene tab whose hierarchy is a chain root, then one node per name nested in the one before.</summary>
    /// <param name="name">The scene and tab name.</param>
    /// <param name="chain">The names of the nodes under the root, each the child of the previous.</param>
    public SceneDocument Open(string name, params string[] chain)
    {
        var prefab = new Prefab { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}.prefab" };
        var root = new Node { Name = name };
        var parent = root;
        foreach (var node in chain)
        {
            var child = new Node { Name = node };
            parent.Children.Add(child);
            parent = child;
        }

        roots[prefab.Id] = root;
        Workspace.Open(prefab);
        return (SceneDocument)Documents.Active!;
    }

    /// <summary>Opens a material tab, which is not a scene.</summary>
    /// <param name="name">The tab name.</param>
    public IDocument OpenMaterial(string name)
    {
        Workspace.Open(new MaterialAsset { Id = Guid.NewGuid(), RelativePath = $"Assets/{name}.mat" });
        return Documents.Active!;
    }

    /// <summary>Adds a prefab to the asset database, as a scene instance's source, without opening it.</summary>
    /// <param name="name">The prefab name.</param>
    public Guid AddPrefab(string name)
    {
        var id = Guid.NewGuid();
        roots[id] = new Node { Name = name };
        Database.MergeDatabase(new Dictionary<Guid, AssetRecord>
        {
            [id] = new()
            {
                AssetId = id,
                AssetTypeName = typeof(Prefab).FullName!,
                SourceRelativePath = $"Assets/{name}.prefab",
            },
        });
        return id;
    }

    /// <summary>The scene root of a tab.</summary>
    public Node Root(SceneDocument tab) => roots[tab.AssetId];

    /// <summary>The node reached from a tab's root by following child names.</summary>
    public Node Node(SceneDocument tab, params string[] path) =>
        path.Aggregate(Root(tab), static (node, name) => node.Children.Single(child => child.Name == name));

    /// <summary>Makes a tab the active one the way a click on its tab does.</summary>
    public void Activate(SceneDocument tab) => Workspace.Activate(tab.Wrapper);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Play.IsActive)
        {
            Play.Stop();
        }

        Bridge.Dispose();
        Memory.Dispose();
        Playing.Dispose();
        Binder.Dispose();
        Undo.Dispose();
        Workspace.Dispose();
        services.Dispose();
    }
}
