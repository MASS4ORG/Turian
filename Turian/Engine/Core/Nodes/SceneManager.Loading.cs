namespace Turian.Engine.Core;

public partial class SceneManager
{
    bool TryApplyDuplicatePolicy(Guid sceneAssetId, out LoadedScene? existing)
    {
        existing = FindLoadedScene(sceneAssetId);
        if (existing is null)
        {
            return false;
        }

        switch (DuplicateLoadPolicy)
        {
            case DuplicateSceneLoadPolicy.ReuseExisting:
                if (!ReferenceEquals(ActiveScene, existing))
                {
                    SetActiveScene(existing);
                }
                return true;

            case DuplicateSceneLoadPolicy.Reject:
                throw new InvalidOperationException(
                    $"Scene asset '{sceneAssetId}' is already loaded and the duplicate load policy is Reject.");

            case DuplicateSceneLoadPolicy.Allow:
            default:
                existing = null;
                return false;
        }
    }

    LoadedScene? FindLoadedScene(Guid assetId)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        lock (scenesLock)
        {
            return loadedScenes.FirstOrDefault(scene => scene.AssetId == assetId);
        }
    }

    async Task<Node> LoadNodeFromProviderAsync(IAssetFileProvider provider, string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            using var stream = provider.GetAssetStream();
            return await DeserializeNodeAsync(stream, sourceDescription).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Failed to load serialized node hierarchy from {sourceDescription}.",
                ex);
        }
    }

    async Task<Node> LoadNodeFromFileAsync(string absolutePath)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            return await DeserializeNodeAsync(stream, absolutePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Failed to load serialized node hierarchy from '{absolutePath}'.",
                ex);
        }
    }

    static async Task<Node> DeserializeNodeAsync(Stream stream, string sourceDescription)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);

        var root = Serializer.LoadData<Node>(json);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize a node hierarchy from '{sourceDescription}'.");
        }

        root.Awake(null);
        return root;
    }

    IAssetFileProvider ResolveProvider(Guid assetId)
    {
        if (!assetDatabase.TryGetAssetProvider(assetId, out var provider) || provider is null)
        {
            throw new FileNotFoundException($"Asset '{assetId}' was not found in the asset database.");
        }

        return provider;
    }

    LoadedScene TrackLoadedScene(Guid assetId, Node root, bool setActive)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Parent = null;

        var loadedScene = new LoadedScene(assetId, root)
        {
            Name = string.IsNullOrWhiteSpace(root.Name) ? "Scene" : root.Name
        };

        lock (scenesLock)
        {
            loadedScenes.Add(loadedScene);

            if (setActive || activeScene is null)
            {
                activeScene = loadedScene;
            }
        }

        return loadedScene;
    }

    Node AttachInstantiatedRoot(Node root, Node? parent)
    {
        ArgumentNullException.ThrowIfNull(root);

        var targetParent = parent ?? ActiveScene?.RootNode;
        if (targetParent is not null)
        {
            root.Parent = targetParent;
            targetParent.Children.Add(root);
        }
        else
        {
            root.Parent = null;
        }

        return root;
    }

    static T FindInstantiatedComponent<T>(Node root, string sourceDescription)
        where T : Component
    {
        if (root.GetComponent<T>() is { } componentOnRoot)
        {
            return componentOnRoot;
        }

        foreach (var component in Node.GetComponentsInChildren<T>(root))
        {
            return component;
        }

        throw new InvalidOperationException(
            $"Instantiated hierarchy from '{sourceDescription}' does not contain a component of type '{typeof(T).FullName}'.");
    }

    static void DetachNodeHierarchy(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        foreach (var child in node.Children.ToList())
        {
            DetachNodeHierarchy(child);
        }

        node.Children.Clear();

        if (node.Parent is not null)
        {
            var parent = node.Parent;
            node.Parent = null;
            parent.Children.Remove(node);
        }
    }

    /// <summary>
    /// Clones a node hierarchy by round-tripping it through the shared JSON serializer.
    /// This guarantees parity with prefab-loaded hierarchies (same converters, same
    /// handling of <c>[JsonIgnore]</c> / <c>[JsonInclude]</c>, no event-handler leaks).
    /// </summary>
    static Node CloneHierarchyViaSerialization(Node source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var json = Serializer.Serialize(source);
        var clone = Serializer.LoadData<Node>(json)
                    ?? throw new InvalidOperationException(
                        $"Failed to clone node hierarchy of type '{source.GetType().FullName}' via serialization.");

        clone.Awake(null);
        return clone;
    }

    static void RecursiveCleanup(Node node)
    {
        node.OnDisable();

        foreach (var component in node.Components.ToList())
        {
            component.Detach();

            if (component is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        foreach (var child in node.Children.ToList())
        {
            RecursiveCleanup(child);
        }

        node.OnDestroy();
    }

    static Guid HashPathToGuid(string absolutePath)
    {
        var normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(normalized), hash);
        return new Guid(hash);
    }
}
