namespace Turian.Tests;

/// <summary>Verifies <see cref="NodePool"/> parking, reuse, callbacks, limits and steady-state allocation.</summary>
public sealed class NodePoolTests
{
    sealed class Resettable : Component, IPoolable
    {
        public int Gets { get; private set; }
        public int Releases { get; private set; }

        public void OnGetFromPool() => Gets++;
        public void OnReleaseToPool() => Releases++;
    }

    static Node Scene()
    {
        var scene = new Node { Name = "Scene" };
        scene.Awake(null);
        return scene;
    }

    static NodePool Pool(Node scene, int maxSize = 8, bool autoExpand = true)
    {
        var root = new Node { Name = "Pool", IsActive = false };
        root.Awake(scene);
        return new NodePool(root, Create, maxSize, autoExpand);
    }

    static Task<Node> Create(Node parent)
    {
        var bullet = new Node { Name = "Bullet" };
        var child = new Node { Name = "Trail" };
        child.Components.Add(new Resettable());
        bullet.Children.Add(child);
        bullet.Components.Add(new Resettable());
        bullet.Awake(parent);
        return Task.FromResult(bullet);
    }

    /// <summary>Prewarmed nodes wait inactive under the pool root, within the maximum size.</summary>
    [Fact]
    public async Task PrewarmParksInactiveNodesUnderTheRoot()
    {
        var pool = Pool(Scene(), maxSize: 3);

        await pool.PrewarmAsync(10);

        Assert.Equal(3, pool.IdleCount);
        Assert.All(pool.Root.Children, static node => Assert.False(node.IsActive));
        Assert.Equal(3, pool.Root.Children.Count);
    }

    /// <summary>A node handed out is active under the caller's parent; released, it is parked again.</summary>
    [Fact]
    public async Task NodesMoveBetweenParentAndRoot()
    {
        var scene = Scene();
        var pool = Pool(scene);
        await pool.PrewarmAsync(1);

        Assert.True(pool.TryGet(out var node, scene));
        Assert.Same(scene, node.Parent);
        Assert.True(node.IsActive);
        Assert.Equal(1, pool.ActiveCount);

        pool.Release(node);

        Assert.Same(pool.Root, node.Parent);
        Assert.False(node.IsActive);
        Assert.Equal(1, pool.IdleCount);
    }

    /// <summary>Poolable components anywhere in the hierarchy reset as the node leaves and returns.</summary>
    [Fact]
    public async Task PoolableComponentsInTheHierarchyAreCalledBack()
    {
        var scene = Scene();
        var pool = Pool(scene);
        await pool.PrewarmAsync(1);
        pool.TryGet(out var node, scene);

        pool.Release(node!);

        var components = new[] { node!.Components.OfType<Resettable>().Single(), node.Children[0].Components.OfType<Resettable>().Single() };
        Assert.All(components, static c => Assert.Equal((1, 1), (c.Gets, c.Releases)));
    }

    /// <summary>A fixed pool never creates on demand, so it runs dry after its prewarmed nodes.</summary>
    [Fact]
    public async Task FixedPoolsRunDry()
    {
        var scene = Scene();
        var pool = Pool(scene, autoExpand: false);
        await pool.PrewarmAsync(2);

        Assert.NotNull(await pool.GetAsync(scene));
        Assert.NotNull(await pool.GetAsync(scene));
        Assert.Null(await pool.GetAsync(scene));
    }

    /// <summary>An expanding pool creates nodes on demand up to the maximum, then runs dry.</summary>
    [Fact]
    public async Task ExpandingPoolsGrowToTheMaximum()
    {
        var scene = Scene();
        var pool = Pool(scene, maxSize: 2);

        Assert.NotNull(await pool.GetAsync(scene));
        Assert.NotNull(await pool.GetAsync(scene));
        Assert.Null(await pool.GetAsync(scene));
        Assert.Equal(2, pool.ActiveCount);
    }

    /// <summary>A node destroyed behind the pool's back is never handed out again.</summary>
    [Fact]
    public async Task DestroyedNodesAreNotReused()
    {
        var scene = Scene();
        var pool = Pool(scene, maxSize: 1);
        await pool.PrewarmAsync(1);
        pool.TryGet(out var node, scene);
        pool.Release(node!);

        node!.DestroyImmediate();

        Assert.False(pool.TryGet(out _, scene));
        Assert.NotNull(await pool.GetAsync(scene));
    }

    /// <summary>Disposing destroys the idle nodes and the root, and nodes released afterwards.</summary>
    [Fact]
    public async Task DisposeDestroysIdleNodesAndTheRoot()
    {
        var scene = Scene();
        var pool = Pool(scene);
        await pool.PrewarmAsync(2);
        pool.TryGet(out var active, scene);
        var idle = pool.Root.Children.Single();

        pool.Dispose();
        pool.Release(active!);
        scene.FlushDestroyQueue();

        Assert.True(idle.IsDestroyed);
        Assert.True(pool.Root.IsDestroyed);
        Assert.True(active!.IsDestroyed);
    }

    /// <summary>Firing a thousand pooled projectiles allocates nothing once the pool is warm.</summary>
    [Fact]
    public async Task SteadyStateAllocatesNothing()
    {
        var scene = Scene();
        var pool = Pool(scene, maxSize: 64);
        await pool.PrewarmAsync(64);
        for (var i = 0; i < 100; i++)
        {
            pool.TryGet(out var warm, scene);
            pool.Release(warm!);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            pool.TryGet(out var bullet, scene);
            pool.Release(bullet!);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
