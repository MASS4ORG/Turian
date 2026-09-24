namespace Turian.Engine.Core;

/// <summary>
/// Manages scene lifecycle operations including loading, unloading, active-scene switching,
/// and hierarchy instantiation.
/// Scenes and prefabs share the same serialized node-hierarchy format; the distinction is
/// whether the loaded hierarchy is tracked as a loaded scene or instantiated under another root.
/// </summary>
/// <remarks>
/// This class is intended to be driven from a single thread (typically the game loop).
/// The internal state is guarded by a lock so that out-of-band calls (editor refreshes,
/// asset watcher callbacks) cannot corrupt the loaded-scene list mid-tick, but lifecycle
/// callbacks on nodes and components are invoked without the lock held.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton, typeof(ISceneManager))]
public partial class SceneManager : ISceneManager
{
    readonly AssetDatabase assetDatabase;
    readonly List<LoadedScene> loadedScenes = [];
    readonly object scenesLock = new();

    /// <summary>
    /// Gets a snapshot of all currently loaded scenes.
    /// </summary>
    public IEnumerable<LoadedScene> LoadedScenes
    {
        get
        {
            lock (scenesLock)
            {
                return loadedScenes.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets or sets the currently active scene.
    /// Only tracked scenes may be assigned.
    /// </summary>
    public LoadedScene? ActiveScene
    {
        get
        {
            lock (scenesLock)
            {
                return activeScene;
            }
        }
        set
        {
            lock (scenesLock)
            {
                if (value is null)
                {
                    activeScene = null;
                    return;
                }

                if (!loadedScenes.Contains(value))
                {
                    throw new InvalidOperationException("The provided scene is not currently tracked by the scene manager.");
                }

                activeScene = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the policy that controls how duplicate scene loads are handled.
    /// </summary>
    public DuplicateSceneLoadPolicy DuplicateLoadPolicy { get; set; } = DuplicateSceneLoadPolicy.ReuseExisting;

    /// <summary>
    /// Gets the root node that persists across all scene loads and unloads.
    /// </summary>
    public Node PersistentRoot { get; } = new() { Name = "PersistentRoot" };

    LoadedScene? activeScene;

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneManager"/> class.
    /// </summary>
    /// <param name="assetDatabase">The asset database used to resolve serialized node assets.</param>
    public SceneManager(AssetDatabase assetDatabase)
    {
        this.assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        PersistentRoot.Awake(null);
    }

    /// <inheritdoc />
    public Task<Node> LoadNodeAsync(Guid assetId)
    {
        return LoadNodeFromProviderAsync(ResolveProvider(assetId), $"asset '{assetId}'");
    }

    /// <inheritdoc />
    public Task<Node> LoadNodeAsync(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!Path.IsPathRooted(absolutePath))
        {
            throw new ArgumentException("Scene path must be absolute.", nameof(absolutePath));
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("Serialized scene/prefab file not found.", absolutePath);
        }

        return LoadNodeFromFileAsync(absolutePath);
    }

    /// <inheritdoc />
    public async Task<LoadedScene> LoadSceneAsync(Guid sceneAssetId, LoadSceneMode mode = LoadSceneMode.Single)
    {
        if (TryApplyDuplicatePolicy(sceneAssetId, out var existing))
        {
            return existing!;
        }

        if (mode == LoadSceneMode.Single)
        {
            UnloadAllScenes();
        }

        var root = await LoadNodeAsync(sceneAssetId).ConfigureAwait(false);
        return TrackLoadedScene(sceneAssetId, root, setActive: true);
    }

    /// <inheritdoc />
    public async Task<LoadedScene> LoadSceneAsync(string absolutePath, LoadSceneMode mode = LoadSceneMode.Single)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!Path.IsPathRooted(absolutePath))
        {
            throw new ArgumentException("Scene path must be absolute.", nameof(absolutePath));
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("Serialized scene/prefab file not found.", absolutePath);
        }

        var syntheticId = HashPathToGuid(absolutePath);

        if (TryApplyDuplicatePolicy(syntheticId, out var existing))
        {
            return existing!;
        }

        if (mode == LoadSceneMode.Single)
        {
            UnloadAllScenes();
        }

        var root = await LoadNodeAsync(absolutePath).ConfigureAwait(false);
        return TrackLoadedScene(syntheticId, root, setActive: true);
    }

    /// <inheritdoc />
    public LoadedScene AdoptScene(Guid sceneAssetId, Node root, LoadSceneMode mode = LoadSceneMode.Single)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (mode == LoadSceneMode.Single)
        {
            UnloadAllScenes();
        }

        return TrackLoadedScene(sceneAssetId, root, setActive: true);
    }

    /// <inheritdoc />
    public bool TryGetLoadedScene(Guid sceneAssetId, out LoadedScene? scene)
    {
        scene = FindLoadedScene(sceneAssetId);
        return scene is not null;
    }

    /// <inheritdoc />
    public bool SetActiveScene(LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        lock (scenesLock)
        {
            if (!loadedScenes.Contains(scene))
            {
                throw new InvalidOperationException("The provided scene is not currently tracked by the scene manager.");
            }

            if (ReferenceEquals(activeScene, scene))
            {
                return false;
            }

            activeScene = scene;
            return true;
        }
    }

    /// <inheritdoc />
    public bool SetActiveScene(Guid sceneAssetId)
    {
        var scene = FindLoadedScene(sceneAssetId)
                    ?? throw new InvalidOperationException($"Scene asset '{sceneAssetId}' is not currently loaded.");

        return SetActiveScene(scene);
    }

    /// <inheritdoc />
    public async Task<Node> InstantiateAsync(Guid sceneAssetId, Node? parent = null)
    {
        var root = await LoadNodeAsync(sceneAssetId).ConfigureAwait(false);
        return AttachInstantiatedRoot(root, parent);
    }

    /// <inheritdoc />
    public Task<Node> InstantiateAsync(AssetReference<Prefab> assetReference, Node? parent = null)
    {
        ArgumentNullException.ThrowIfNull(assetReference);

        if (assetReference.IsEmpty)
        {
            throw new InvalidOperationException("Cannot instantiate from an empty asset reference.");
        }

        return InstantiateAsync(assetReference.AssetId, parent);
    }

    /// <inheritdoc />
    public async Task<Node> InstantiateAsync(string absolutePath, Node? parent = null)
    {
        var root = await LoadNodeAsync(absolutePath).ConfigureAwait(false);
        return AttachInstantiatedRoot(root, parent);
    }

    /// <inheritdoc />
    public Task<T> InstantiateAsync<T>(T componentTemplate, Node? parent = null)
        where T : Component
    {
        ArgumentNullException.ThrowIfNull(componentTemplate);

        if (!componentTemplate.IsAttached)
        {
            throw new InvalidOperationException(
                $"Cannot instantiate from a detached component of type '{componentTemplate.GetType().FullName}'. " +
                "Hold the prefab via a PrefabReference<T> and call InstantiateAsync on it instead.");
        }

        var sourceNode = componentTemplate.Node
            ?? throw new InvalidOperationException("Component is not attached to a node.");
        var clonedRoot = CloneHierarchyViaSerialization(sourceNode);
        AttachInstantiatedRoot(clonedRoot, parent);

        var sourceDescription = componentTemplate.GetType().FullName ?? componentTemplate.GetType().Name;
        return Task.FromResult(FindInstantiatedComponent<T>(clonedRoot, sourceDescription));
    }

    /// <inheritdoc />
    public void UnloadScene(LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        lock (scenesLock)
        {
            if (!loadedScenes.Remove(scene))
            {
                return;
            }

            if (ReferenceEquals(activeScene, scene))
            {
                activeScene = loadedScenes.FirstOrDefault();
            }
        }

        DetachNodeHierarchy(scene.RootNode);
        RecursiveCleanup(scene.RootNode);
        scene.ResetRuntimeState();
    }

    /// <inheritdoc />
    public bool UnloadScene(Guid sceneAssetId)
    {
        var scene = FindLoadedScene(sceneAssetId);
        if (scene is null)
        {
            return false;
        }

        UnloadScene(scene);
        return true;
    }

    /// <inheritdoc />
    public void UnloadAllScenes()
    {
        LoadedScene[] toUnload;
        lock (scenesLock)
        {
            toUnload = [.. loadedScenes];
        }

        foreach (var scene in toUnload)
        {
            UnloadScene(scene);
        }
    }

    /// <summary>
    /// Ensures the deferred start phase is executed once for every tracked scene.
    /// </summary>
    /// <remarks>
    /// Called once per frame by the game loop, after any in-flight scene load has completed.
    /// Safe to call when no scenes are loaded.
    /// </remarks>
    public void EnsureScenesStarted()
    {
        LoadedScene[] snapshot;
        lock (scenesLock)
        {
            snapshot = [.. loadedScenes];
        }

        foreach (var scene in snapshot)
        {
            if (scene.HasStarted)
            {
                continue;
            }

            scene.RootNode.Start();
            scene.MarkStarted();
        }
    }

}
