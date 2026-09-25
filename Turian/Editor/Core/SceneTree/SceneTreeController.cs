namespace Turian.Editor.Core;

/// <summary>
/// Owns the scene graph state for the scene tree panel: loading, saving,
/// node CRUD, selection memory, and asset dirty-marking.
/// </summary>
public sealed class SceneTreeController(
    AssetManager assetManager,
    SettingsService settingsService,
    AssetImporter assetImporter)
    : IPlaySceneHost
{
    readonly Dictionary<Guid, Node> loadedSceneRoots = [];
    readonly Dictionary<Guid, Guid> selectedNodeIdsByAssetId = [];

    Node? sceneRoot;
    Node? runtimeRoot;

    /// <summary>Gets the currently opened asset, if any.</summary>
    public Asset? CurrentAsset { get; private set; }

    /// <summary>
    /// Gets the scene root the editor panels should display, or <c>null</c> when no scene is open.
    /// While play mode is running this is the <em>running</em> hierarchy, so the Scene Tree and
    /// Inspector show live objects.
    /// </summary>
    public Node? CurrentSceneRoot => runtimeRoot ?? sceneRoot;

    /// <summary>Gets the scene root being edited, ignoring any running play session.</summary>
    public Node? EditorSceneRoot => sceneRoot;

    /// <summary>
    /// Gets a value indicating whether the panels are currently showing a running play session
    /// rather than the scene being edited.
    /// </summary>
    public bool IsShowingRuntimeScene => runtimeRoot is not null;

    /// <summary>
    /// Points the editor panels at <paramref name="root"/>, the hierarchy of a running play session.
    /// Called again whenever game code loads a different scene, so the tree keeps following the game.
    /// </summary>
    public void ShowRuntimeScene(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (ReferenceEquals(runtimeRoot, root)) return;

        runtimeRoot = root;
        SceneLoaded?.Invoke(runtimeRoot);
        SelectionRestoreRequested?.Invoke(RememberedSelection());
    }

    /// <summary>
    /// Points the editor panels back at the scene being edited. Any value the running game changed
    /// disappears with the session, because play never touched the edited hierarchy.
    /// </summary>
    public void ShowEditorScene()
    {
        if (runtimeRoot is null) return;

        runtimeRoot = null;
        SceneLoaded?.Invoke(sceneRoot);
        SelectionRestoreRequested?.Invoke(RememberedSelection());
    }

    Guid? RememberedSelection() =>
        CurrentAsset is not null && selectedNodeIdsByAssetId.TryGetValue(CurrentAsset.Id, out var id)
            ? id
            : null;

    /// <summary>Raised when the scene structure changed and the UI tree should be rebuilt.</summary>
    public event Action<Node?>? SceneLoaded;

    /// <summary>Raised when the previously selected node should be restored.</summary>
    public event Action<Guid?>? SelectionRestoreRequested;

    /// <summary>Raised when a viewport should center its camera on the given node (e.g. scene-tree double-click).</summary>
    public event Action<Node, FrameNodeOptions>? FrameNodeRequested;

    /// <summary>Asks listeners to frame <paramref name="node"/> in their viewport.</summary>
    public void RequestFrameNode(Node node, FrameNodeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        FrameNodeRequested?.Invoke(node, options);
    }

    /// <summary>
    /// Drops the cached scene root and selection for <paramref name="assetId"/> when its tab is closed.
    /// Prevents unbounded memory growth for projects with many prefabs.
    /// </summary>
    public void CloseAsset(Guid assetId)
    {
        loadedSceneRoots.Remove(assetId);
        selectedNodeIdsByAssetId.Remove(assetId);

        if (CurrentAsset?.Id != assetId) return;

        // Closing the open scene empties the tree; a host that closed a background tab keeps its view.
        CurrentAsset = null;
        sceneRoot = null;
        SceneLoaded?.Invoke(CurrentSceneRoot);
    }

    /// <summary>Resets all state (called on project reload).</summary>
    public void Reset()
    {
        loadedSceneRoots.Clear();
        selectedNodeIdsByAssetId.Clear();
        sceneRoot = null;
        runtimeRoot = null;
        CurrentAsset = null;
        SceneLoaded?.Invoke(null);
    }

    /// <summary>
    /// Round-trips every cached scene root through JSON so that Node/Component
    /// instances are rebuilt against the currently-loaded user assembly. Must be
    /// called after user code is recompiled and <see cref="Serializer.ResetOptions"/>
    /// has been invoked — otherwise cached trees keep references to stale Types
    /// from the previous (unloaded) assembly load context.
    /// </summary>
    public void RebindLoadedScenes()
    {
        if (loadedSceneRoots.Count == 0) return;

        var assetIds = loadedSceneRoots.Keys.ToArray();
        foreach (var assetId in assetIds)
        {
            var oldRoot = loadedSceneRoots[assetId];
            try
            {
                var newRoot = NodeCloner.DeepClone(oldRoot);
                if (newRoot is null)
                {
                    Log.Logger.LogWarning("RebindLoadedScenes: serializer returned null for asset {AssetId}", assetId);
                    continue;
                }

                loadedSceneRoots[assetId] = newRoot;

                if (CurrentAsset?.Id == assetId)
                    sceneRoot = newRoot;
            }
            catch (Exception ex)
            {
                Log.Logger.LogError(ex, "RebindLoadedScenes: failed to rebind scene for asset {AssetId}", assetId);
            }
        }

        SceneLoaded?.Invoke(CurrentSceneRoot);
        SelectionRestoreRequested?.Invoke(RememberedSelection());
    }

    /// <summary>Activates <paramref name="asset"/>, loading its scene root if it is a <see cref="Prefab"/>.</summary>
    public void OpenAsset(Asset? asset)
    {
        CurrentAsset = asset;
        sceneRoot = asset is Prefab prefab ? GetOrLoadSceneRoot(prefab) : null;

        if (asset is Prefab prefabAsset && sceneRoot is null)
        {
            Log.Logger.LogError(
                "SceneTree failed to load prefab asset {AssetId} at {RelativePath}. The scene tree will remain empty",
                prefabAsset.Id,
                prefabAsset.RelativePath);
        }

        // While a play session is running the panels keep showing the running hierarchy; the newly
        // opened asset becomes visible once play stops.
        SceneLoaded?.Invoke(CurrentSceneRoot);
        SelectionRestoreRequested?.Invoke(RememberedSelection());
    }

    /// <summary>Serializes the scene root of <paramref name="asset"/> to disk.</summary>
    public void SaveAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset is not Prefab prefab || settingsService.Settings is null) return;
        if (!loadedSceneRoots.TryGetValue(prefab.Id, out var root)) return;

        try
        {
            var path = Path.Combine(settingsService.Settings.ProjectAbsoluteDir, prefab.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Serializer.Save(path, root);
            assetImporter.ReimportNow(path, overwriteExisting: true);
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Failed to save scene {RelativePath}", prefab.RelativePath);
            asset.MarkModified();
        }
    }

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Persists the selected node for the current asset and notifies listeners.</summary>
    public void SelectNode(Node? node)
    {
        if (CurrentAsset is not null && node is not null)
            selectedNodeIdsByAssetId[CurrentAsset.Id] = node.Id;
        assetManager.OpenNode(node);
    }

    /// <summary>Notifies listeners that the selected node was updated in-place.</summary>
    public void NotifySelectedNodeUpdated() => assetManager.UpdateSelectedNode();

    // ── Node CRUD ─────────────────────────────────────────────────────────────

    /// <summary>Creates a new empty <see cref="Node"/> with a unique name.</summary>
    public Node CreateNode(IEnumerable<string> siblingNames)
        => new() { Name = NodeNaming.GetNextAvailable("Node", siblingNames) };

    /// <summary>Renames <paramref name="node"/>, deduplicating against <paramref name="siblingNames"/>.</summary>
    public void RenameNode(Node node, string requestedName, IEnumerable<string> siblingNames)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Name = NodeNaming.GetNextAvailable(requestedName, siblingNames);
    }

    /// <summary>
    /// Attaches <paramref name="node"/> as a child of <paramref name="parent"/>
    /// (or as a root when parent is <see langword="null"/>) at <paramref name="index"/>.
    /// Returns the engine-side parent node, or <see langword="null"/> for root.
    /// </summary>
    public void AttachNode(Node node, Node? parent, int index)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Parent = parent;
        if (parent is not null)
        {
            var safeIndex = Math.Clamp(index, 0, parent.Children.Count);
            parent.Children.Insert(safeIndex, node);
        }
    }

    /// <summary>Detaches <paramref name="node"/> from its current parent.</summary>
    public void DetachNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Parent?.Children.Remove(node);
        node.Parent = null;
    }

    /// <summary>
    /// Clones <paramref name="source"/> with <paramref name="newName"/>.
    /// The clone is detached (no parent set).
    /// </summary>
    public Node CloneNode(Node source, string newName) => NodeCloner.Clone(source, newName);

    /// <summary>
    /// Marks the current asset as modified. Does nothing while a play session is displayed:
    /// edits made to running objects are discarded when play stops, so they must not dirty the
    /// saved scene.
    /// </summary>
    public void MarkAssetModified()
    {
        if (IsShowingRuntimeScene) return;

        if (CurrentAsset is not null)
            assetManager.AlterAsset(CurrentAsset);
    }

    /// <summary>Notifies listeners that <paramref name="node"/> changed in-place.</summary>
    public void RefreshNode(Node? node)
    {
        assetManager.RefreshNode(node);
        if (node is not null && assetManager.GetSelectedNodeForAsset(CurrentAsset) is { } sel
            && ReferenceEquals(sel, node))
            assetManager.UpdateSelectedNode();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Searches the loaded tree for a node by ID.</summary>
    public Node? FindNodeById(Guid nodeId)
        => CurrentSceneRoot is not null ? FindNodeById(CurrentSceneRoot, nodeId) : null;

    static Node? FindNodeById(Node current, Guid id)
    {
        if (current.Id == id) return current;
        foreach (var child in current.Children)
        {
            var found = FindNodeById(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    Node? GetOrLoadSceneRoot(Prefab prefab)
    {
        if (loadedSceneRoots.TryGetValue(prefab.Id, out var existing))
        {
            return existing;
        }

        try
        {
            var root = TryLoadWithSceneManager(prefab);
            if (root is not null)
            {
                loadedSceneRoots[prefab.Id] = root;
                Log.Logger.LogDebug(
                    "SceneTree loaded prefab root {NodeId} for asset {AssetId} at {RelativePath}",
                    root.Id,
                    prefab.Id,
                    prefab.RelativePath);
                return root;
            }

            Log.Logger.LogWarning(
                "SceneManager could not load scene asset {AssetId} at {RelativePath}. Trying path fallback",
                prefab.Id,
                prefab.RelativePath);
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(
                ex,
                "Failed to load scene tree prefab asset {AssetId} at {RelativePath} via SceneManager. Trying fallback load",
                prefab.Id,
                prefab.RelativePath);
        }

        var fallbackRoot = TryLoadSceneRootFromRelativePath(prefab);
        if (fallbackRoot is not null)
        {
            loadedSceneRoots[prefab.Id] = fallbackRoot;
        }

        return fallbackRoot;
    }

    static Node? TryLoadWithSceneManager(Prefab prefab)
    {
        var sceneManager = RuntimeServices.TryGet<ISceneManager>();
        if (sceneManager is null)
        {
            return null;
        }

        return sceneManager.LoadNodeAsync(prefab.Id).GetAwaiter().GetResult();
    }

    Node? TryLoadSceneRootFromRelativePath(Prefab prefab)
    {
        if (settingsService.Settings is null)
        {
            Log.Logger.LogError(
                "Cannot fallback-load scene asset {AssetId} at {RelativePath} because editor settings are unavailable",
                prefab.Id,
                prefab.RelativePath);
            return null;
        }

        var absolutePath = Path.Combine(settingsService.Settings.ProjectAbsoluteDir, prefab.RelativePath);
        if (!File.Exists(absolutePath))
        {
            Log.Logger.LogError(
                "Fallback scene load failed for asset {AssetId}: file does not exist at {AbsolutePath}",
                prefab.Id,
                absolutePath);
            return null;
        }

        try
        {
            var sceneManager = RuntimeServices.TryGet<ISceneManager>();
            if (sceneManager is not null)
            {
                var root = sceneManager.LoadNodeAsync(absolutePath).GetAwaiter().GetResult();
                Log.Logger.LogInformation(
                    "SceneTree fallback-loaded prefab root {NodeId} for asset {AssetId} from {AbsolutePath}",
                    root.Id,
                    prefab.Id,
                    absolutePath);
                return root;
            }
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(
                ex,
                "Fallback SceneManager load failed for scene asset {AssetId} from {AbsolutePath}",
                prefab.Id,
                absolutePath);
        }

        try
        {
            var json = File.ReadAllText(absolutePath, Encoding.UTF8);
            var root = Serializer.LoadData<Node>(json);
            if (root is null)
            {
                Log.Logger.LogError(
                    "Serializer.LoadData returned null during final fallback for scene asset {AssetId} from {AbsolutePath}",
                    prefab.Id,
                    absolutePath);
                return null;
            }

            root.Awake(null);
            Log.Logger.LogInformation(
                "SceneTree final fallback deserialized prefab root {NodeId} for asset {AssetId} from {AbsolutePath}",
                root.Id,
                prefab.Id,
                absolutePath);
            return root;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(
                ex,
                "Final fallback deserialization failed for scene asset {AssetId} from {AbsolutePath}",
                prefab.Id,
                absolutePath);
            return null;
        }
    }
}

/// <summary>Options for framing a node in the viewport.</summary>
public record struct FrameNodeOptions
{
    /// <summary>
    /// When <c>true</c>, the viewport camera should also match the target node's orientation
    /// (useful for camera nodes — lets you "look through" them).
    /// </summary>
    public bool MatchRotation { get; init; }
}
