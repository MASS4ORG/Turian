namespace Turian.Engine.Core;

/// <summary>
/// Defines the policy used when loading a scene that is already tracked by the scene manager.
/// </summary>
public enum DuplicateSceneLoadPolicy
{
    /// <summary>
    /// Allows loading the same scene asset multiple times as distinct loaded scene instances.
    /// </summary>
    Allow,

    /// <summary>
    /// Reuses the existing loaded scene instance when the same scene asset is requested again.
    /// </summary>
    ReuseExisting,

    /// <summary>
    /// Throws an exception when attempting to load a scene asset that is already loaded.
    /// </summary>
    Reject
}

/// <summary>
/// Provides centralized scene and prefab loading, scene lifecycle management,
/// active-scene switching, and hierarchy instantiation operations.
/// </summary>
/// <remarks>
/// Implementations are expected to be driven from the game loop's thread. A single
/// <see cref="SceneManager"/> instance services both scene and prefab operations since
/// scenes and prefabs share the same serialized node-hierarchy format.
/// </remarks>
public interface ISceneManager
{
    /// <summary>
    /// Gets a snapshot of all currently loaded scenes.
    /// </summary>
    IEnumerable<LoadedScene> LoadedScenes { get; }

    /// <summary>
    /// Gets or sets the currently active scene.
    /// Implementations must reject values that are not currently tracked by the scene manager.
    /// </summary>
    LoadedScene? ActiveScene { get; set; }

    /// <summary>
    /// Gets or sets the policy that controls how duplicate scene load requests are handled.
    /// </summary>
    DuplicateSceneLoadPolicy DuplicateLoadPolicy { get; set; }

    /// <summary>
    /// Gets the root node that persists across all scene loads and unloads.
    /// </summary>
    Node PersistentRoot { get; }

    /// <summary>
    /// Loads and deserializes a node hierarchy from the asset database without
    /// attaching it to the loaded scene list.
    /// </summary>
    /// <param name="assetId">The unique identifier of the prefab or scene asset to load.</param>
    /// <returns>A task that represents the asynchronous operation, containing the loaded root node.</returns>
    Task<Node> LoadNodeAsync(Guid assetId);

    /// <summary>
    /// Loads and deserializes a node hierarchy from an absolute file path without
    /// attaching it to the loaded scene list.
    /// </summary>
    /// <param name="absolutePath">The absolute path to the serialized node file.</param>
    /// <returns>A task that represents the asynchronous operation, containing the loaded root node.</returns>
    Task<Node> LoadNodeAsync(string absolutePath);

    /// <summary>
    /// Asynchronously loads a scene from the specified asset.
    /// Duplicate handling is controlled by <see cref="DuplicateLoadPolicy"/>.
    /// Single-mode loads unload all currently tracked scenes before loading the new scene,
    /// unless the implementation reuses an already loaded matching scene.
    /// </summary>
    Task<LoadedScene> LoadSceneAsync(Guid sceneAssetId, LoadSceneMode mode = LoadSceneMode.Single);

    /// <summary>
    /// Loads a scene from disk and tracks it as a loaded scene.
    /// The scene is keyed by a deterministic hash of the absolute path so that
    /// <see cref="TryGetLoadedScene"/> can find it on subsequent calls.
    /// </summary>
    Task<LoadedScene> LoadSceneAsync(string absolutePath, LoadSceneMode mode = LoadSceneMode.Single);

    /// <summary>
    /// Tracks an already-deserialized hierarchy as a loaded scene, without reading it from disk.
    /// The adopted scene becomes the active scene.
    /// </summary>
    /// <remarks>
    /// Used by the Studio's play mode, which runs on an in-memory copy of the scene being edited
    /// rather than on the last version written to disk.
    /// </remarks>
    /// <param name="sceneAssetId">The identifier to track the scene under.</param>
    /// <param name="root">The root node of the hierarchy to adopt.</param>
    /// <param name="mode">Whether to unload the currently tracked scenes first.</param>
    LoadedScene AdoptScene(Guid sceneAssetId, Node root, LoadSceneMode mode = LoadSceneMode.Single);

    /// <summary>
    /// Attempts to find a tracked loaded scene by asset identifier.
    /// </summary>
    bool TryGetLoadedScene(Guid sceneAssetId, out LoadedScene? scene);

    /// <summary>
    /// Sets the currently active scene.
    /// </summary>
    bool SetActiveScene(LoadedScene scene);

    /// <summary>
    /// Sets the currently active scene by asset identifier.
    /// </summary>
    bool SetActiveScene(Guid sceneAssetId);

    /// <summary>
    /// Instantiates a prefab or scene hierarchy under the provided parent node.
    /// If <paramref name="parent"/> is null, the instantiated root is attached to
    /// the active scene root when available.
    /// </summary>
    Task<Node> InstantiateAsync(Guid sceneAssetId, Node? parent = null);

    /// <summary>
    /// Instantiates a prefab or scene hierarchy referenced by an asset reference.
    /// If <paramref name="parent"/> is null, the instantiated root is attached to
    /// the active scene root when available.
    /// </summary>
    /// <remarks>
    /// For type-safe instantiation that returns a specific component on the instantiated root,
    /// declare the field as <c>PrefabReference&lt;TComponent&gt;</c> and call its
    /// <c>InstantiateAsync</c> method instead of this overload.
    /// </remarks>
    Task<Node> InstantiateAsync(AssetReference<Prefab> assetReference, Node? parent = null);

    /// <summary>
    /// Instantiates a prefab or scene hierarchy from disk under the provided parent node.
    /// </summary>
    Task<Node> InstantiateAsync(string absolutePath, Node? parent = null);

    /// <summary>
    /// Clones the hierarchy that owns the given attached component and returns the
    /// corresponding component on the cloned tree. This overload is only valid for
    /// components currently attached to a live scene hierarchy; detached templates
    /// must instead be loaded via a <c>PrefabReference&lt;TComponent&gt;</c>.
    /// </summary>
    Task<T> InstantiateAsync<T>(T componentTemplate, Node? parent = null)
        where T : Component;

    /// <summary>
    /// Unloads the specified scene and cleans up its resources.
    /// </summary>
    void UnloadScene(LoadedScene scene);

    /// <summary>
    /// Unloads a tracked scene by asset identifier.
    /// </summary>
    bool UnloadScene(Guid sceneAssetId);

    /// <summary>
    /// Unloads all currently loaded scenes.
    /// </summary>
    void UnloadAllScenes();

    /// <summary>
    /// Runs the deferred <c>Start</c> phase once for every tracked scene.
    /// Expected to be invoked at the start of every game-loop tick.
    /// </summary>
    void EnsureScenesStarted();
}
