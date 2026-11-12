namespace Turian.Tests;

/// <summary>Verifies <see cref="ObjectPool{T}"/> reuse, limits, callbacks and steady-state allocation.</summary>
public sealed class ObjectPoolTests
{
    sealed class Bullet : IPoolable
    {
        public int Gets { get; private set; }
        public int Releases { get; private set; }
        public bool Dead { get; set; }

        public void OnGetFromPool() => Gets++;
        public void OnReleaseToPool() => Releases++;
    }

    /// <summary>A released object comes back from the next get instead of a new one being made.</summary>
    [Fact]
    public void ReleasedObjectsAreReused()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet());
        var first = pool.Get();

        pool.Release(first);

        Assert.Same(first, pool.Get());
        Assert.Equal(1, pool.OwnedCount);
    }

    /// <summary>Poolable objects reset on the way out and on the way back.</summary>
    [Fact]
    public void PoolableObjectsAreCalledBack()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet());
        var bullet = pool.Get();
        pool.Release(bullet);
        pool.Get();

        Assert.Equal(2, bullet.Gets);
        Assert.Equal(1, bullet.Releases);
    }

    /// <summary>The maximum bounds the objects owned, idle and handed out together.</summary>
    [Fact]
    public void MaxSizeBoundsTheOwnedObjects()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet(), maxSize: 2);
        pool.Get();
        pool.Get();

        Assert.False(pool.TryGet(out _));
        Assert.Throws<InvalidOperationException>(pool.Get);
        Assert.Equal(2, pool.ActiveCount);
    }

    /// <summary>Without auto-expand only the prewarmed objects are handed out.</summary>
    [Fact]
    public void FixedPoolsHandOutOnlyPrewarmedObjects()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet(), maxSize: 10, autoExpand: false);

        Assert.Equal(3, pool.Prewarm(3));
        pool.Get();
        pool.Get();
        pool.Get();

        Assert.False(pool.TryGet(out _));
    }

    /// <summary>Prewarming stops at the maximum size.</summary>
    [Fact]
    public void PrewarmStopsAtMaxSize()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet(), maxSize: 4);

        Assert.Equal(4, pool.Prewarm(10));
        Assert.Equal(4, pool.IdleCount);
    }

    /// <summary>Releasing twice or releasing a foreign object is an error, not silent corruption.</summary>
    [Fact]
    public void InvalidReleasesAreRejected()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet());
        var bullet = pool.Get();
        pool.Release(bullet);

        Assert.Throws<InvalidOperationException>(() => pool.Release(bullet));
        Assert.Throws<ArgumentException>(() => pool.Release(new Bullet()));
    }

    /// <summary>An object that died while idle or handed out is dropped and its place is freed.</summary>
    [Fact]
    public void DeadObjectsAreDropped()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet(), maxSize: 2, isAlive: static b => !b.Dead);
        var idle = pool.Get();
        var active = pool.Get();
        pool.Release(idle);
        idle.Dead = true;
        active.Dead = true;

        pool.Release(active);

        Assert.Equal(1, pool.OwnedCount);
        Assert.NotSame(idle, pool.Get());
        Assert.Equal(1, pool.OwnedCount);
    }

    /// <summary>Clearing discards idle objects through the dispose callback and keeps those handed out.</summary>
    [Fact]
    public void ClearDiscardsIdleObjects()
    {
        List<Bullet> disposed = [];
        var pool = new ObjectPool<Bullet>(static () => new Bullet(), onDispose: disposed.Add);
        var kept = pool.Get();
        var gone = pool.Get();
        pool.Release(gone);

        pool.Clear();

        Assert.Equal([gone], disposed);
        Assert.Equal(1, pool.OwnedCount);
        pool.Release(kept);
    }

    /// <summary>After warm-up, getting and releasing allocates nothing.</summary>
    [Fact]
    public void SteadyStateAllocatesNothing()
    {
        var pool = new ObjectPool<Bullet>(static () => new Bullet());
        pool.Prewarm(64);
        for (var i = 0; i < 100; i++)
        {
            pool.Release(pool.Get());
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            pool.Release(pool.Get());
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
