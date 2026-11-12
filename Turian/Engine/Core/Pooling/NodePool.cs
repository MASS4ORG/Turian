using System.Diagnostics.CodeAnalysis;

namespace Turian.Engine.Core;

/// <summary>
/// Reuses node hierarchies such as bullets or list rows instead of instantiating and destroying them. An idle node is
/// inactive and parked under <see cref="Root"/>; a node handed out is active under the parent the caller names.
/// Reusing an idle node allocates nothing, so only creation, which loads or clones the hierarchy, is asynchronous.
/// </summary>
public sealed class NodePool : IDisposable
{
    readonly ObjectPool<Node> pool;
    readonly Func<Node, Task<Node>> instantiate;
    int pending;
    bool disposed;

    /// <summary>Creates a pool over a custom way of making nodes.</summary>
    /// <param name="root">The inactive node idle nodes are parked under; it is destroyed with the pool.</param>
    /// <param name="instantiate">Creates a new node under the parent it is given.</param>
    /// <param name="maxSize">The most nodes the pool owns at once, idle and handed out together.</param>
    /// <param name="autoExpand">Whether <see cref="GetAsync"/> may create nodes beyond those prewarmed.</param>
    public NodePool(Node root, Func<Node, Task<Node>> instantiate, int maxSize = 1024, bool autoExpand = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(instantiate);
        Root = root;
        this.instantiate = instantiate;
        pool = new ObjectPool<Node>(
            maxSize: maxSize,
            autoExpand: autoExpand,
            onGet: static node => Invoke(node, acquired: true),
            onRelease: Park,
            onDispose: static node => node.Destroy(),
            isAlive: static node => !node.IsDestroyed);
    }

    /// <summary>The inactive node that holds idle nodes out of every scene's updates and queries.</summary>
    public Node Root { get; }

    /// <summary>The most nodes the pool owns at once.</summary>
    public int MaxSize => pool.MaxSize;

    /// <summary>Nodes waiting in the pool.</summary>
    public int IdleCount => pool.IdleCount;

    /// <summary>Nodes handed out and not yet released.</summary>
    public int ActiveCount => pool.ActiveCount;

    /// <summary>Creates a pool of instances of a prefab, parked under an inactive node of the persistent root.</summary>
    /// <param name="scenes">The scene manager that instantiates the prefab.</param>
    /// <param name="prefab">The prefab each pooled node is an instance of.</param>
    /// <param name="maxSize">The most nodes the pool owns at once, idle and handed out together.</param>
    /// <param name="autoExpand">Whether <see cref="GetAsync"/> may create nodes beyond those prewarmed.</param>
    /// <returns>The pool, empty until <see cref="PrewarmAsync"/> or <see cref="GetAsync"/> fills it.</returns>
    public static NodePool ForPrefab(
        ISceneManager scenes, AssetReference<Prefab> prefab, int maxSize = 1024, bool autoExpand = true)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        var root = new Node { Name = "Pool", IsActive = false };
        root.Awake(scenes.PersistentRoot);
        return new NodePool(root, parent => scenes.InstantiateAsync(prefab, parent), maxSize, autoExpand);
    }

    /// <summary>Creates idle nodes ahead of use, within <see cref="MaxSize"/>, so later gets do not stall.</summary>
    /// <param name="count">How many nodes to create.</param>
    /// <returns>A task that completes when they are ready.</returns>
    public async Task PrewarmAsync(int count)
    {
        for (var i = 0; i < count && Reserve(); i++)
        {
            Adopt(await CreateAsync());
        }
    }

    /// <summary>Hands out an idle node without creating any, so it never allocates.</summary>
    /// <param name="node">The node, active under <paramref name="parent"/>, once handed out.</param>
    /// <param name="parent">Where the node goes; null leaves it without a parent.</param>
    /// <returns>False when no node is idle.</returns>
    public bool TryGet([NotNullWhen(true)] out Node? node, Node? parent = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!pool.TryGet(out node))
        {
            return false;
        }

        node.Parent = parent;
        node.IsActive = true;
        return true;
    }

    /// <summary>Hands out an idle node, or creates one when the pool may still grow.</summary>
    /// <param name="parent">Where the node goes; null leaves it without a parent.</param>
    /// <returns>The node, or null when every node the pool may own is handed out.</returns>
    public async ValueTask<Node?> GetAsync(Node? parent = null)
    {
        if (TryGet(out var node, parent))
        {
            return node;
        }

        if (!pool.AutoExpand || !Reserve())
        {
            return null;
        }

        Adopt(await CreateAsync());
        return TryGet(out node, parent) ? node : null;
    }

    /// <summary>Returns a node to the pool: it is reset, deactivated and parked under <see cref="Root"/>.</summary>
    /// <param name="node">A node this pool handed out. A destroyed node is dropped instead.</param>
    public void Release(Node node)
    {
        if (disposed)
        {
            node.Destroy();
            return;
        }

        pool.Release(node);
    }

    /// <summary>Destroys the idle nodes and <see cref="Root"/>; nodes handed out are destroyed when released.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        pool.Clear();
        Root.Destroy();
        disposed = true;
    }

    // Reserves room for a node about to be created, so concurrent creations cannot exceed the maximum.
    bool Reserve()
    {
        if (pool.OwnedCount + pending >= pool.MaxSize)
        {
            return false;
        }

        pending++;
        return true;
    }

    async Task<Node> CreateAsync()
    {
        try
        {
            var node = await instantiate(Root);
            node.IsActive = false;
            return node;
        }
        catch
        {
            pending--;
            throw;
        }
    }

    void Adopt(Node node)
    {
        pending--;
        pool.Adopt(node);
    }

    void Park(Node node)
    {
        Invoke(node, acquired: false);
        node.IsActive = false;
        node.Parent = Root;
    }

    static void Invoke(Node node, bool acquired)
    {
        foreach (var component in node.Components)
        {
            if (component is not IPoolable poolable)
            {
                continue;
            }

            if (acquired)
            {
                poolable.OnGetFromPool();
            }
            else
            {
                poolable.OnReleaseToPool();
            }
        }

        foreach (var child in node.Children)
        {
            Invoke(child, acquired);
        }
    }
}
