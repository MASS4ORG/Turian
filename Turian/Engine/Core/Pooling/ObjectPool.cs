using System.Diagnostics.CodeAnalysis;

namespace Turian.Engine.Core;

/// <summary>
/// Reuses objects instead of allocating them again: after warm-up, <see cref="TryGet"/> and <see cref="Release"/>
/// allocate nothing. The pool is not thread-safe and belongs to one thread, as the game loop does.
/// </summary>
/// <typeparam name="T">The pooled type; instances implementing <see cref="IPoolable"/> are called back.</typeparam>
public sealed class ObjectPool<T> where T : class
{
    readonly Stack<T> idle = new();
    readonly HashSet<T> idleSet = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<T> owned = new(ReferenceEqualityComparer.Instance);
    readonly Func<T>? create;
    readonly Action<T>? onGet;
    readonly Action<T>? onRelease;
    readonly Action<T>? onDispose;
    readonly Func<T, bool>? isAlive;

    /// <summary>Creates a pool.</summary>
    /// <param name="create">Makes a new object when none is idle; null for a pool filled only through <see cref="Adopt"/>.</param>
    /// <param name="maxSize">The most objects the pool owns at once, idle and handed out together.</param>
    /// <param name="autoExpand">Whether <see cref="TryGet"/> may create objects beyond those already adopted.</param>
    /// <param name="onGet">Runs on an object as it leaves the pool.</param>
    /// <param name="onRelease">Runs on an object as it returns to the pool.</param>
    /// <param name="onDispose">Runs on each idle object that <see cref="Clear"/> discards.</param>
    /// <param name="isAlive">Tells whether an object can still be used; dead ones are dropped instead of reused.</param>
    public ObjectPool(
        Func<T>? create = null,
        int maxSize = 1024,
        bool autoExpand = true,
        Action<T>? onGet = null,
        Action<T>? onRelease = null,
        Action<T>? onDispose = null,
        Func<T, bool>? isAlive = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);
        this.create = create;
        MaxSize = maxSize;
        AutoExpand = autoExpand;
        this.onGet = onGet;
        this.onRelease = onRelease;
        this.onDispose = onDispose;
        this.isAlive = isAlive;
    }

    /// <summary>The most objects the pool owns at once, idle and handed out together.</summary>
    public int MaxSize { get; }

    /// <summary>Whether the pool creates objects on demand once its idle ones are used up.</summary>
    public bool AutoExpand { get; }

    /// <summary>Objects waiting in the pool.</summary>
    public int IdleCount => idle.Count;

    /// <summary>Objects handed out and not yet released.</summary>
    public int ActiveCount => owned.Count - idle.Count;

    /// <summary>Every object the pool accounts for, idle and handed out.</summary>
    public int OwnedCount => owned.Count;

    /// <summary>Whether the pool has room to own another object.</summary>
    public bool CanGrow => owned.Count < MaxSize;

    /// <summary>Creates up to <paramref name="count"/> objects and keeps them idle, within <see cref="MaxSize"/>.</summary>
    /// <param name="count">How many objects to add.</param>
    /// <returns>How many were created.</returns>
    public int Prewarm(int count)
    {
        if (create is null)
        {
            throw new InvalidOperationException("The pool has no factory to create objects with.");
        }

        var created = 0;
        while (created < count && CanGrow)
        {
            Adopt(create());
            created++;
        }

        return created;
    }

    /// <summary>Takes an object created elsewhere into the pool as an idle one.</summary>
    /// <param name="item">The object to own.</param>
    public void Adopt(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanGrow)
        {
            throw new InvalidOperationException($"The pool already owns its maximum of {MaxSize} objects.");
        }

        if (!owned.Add(item))
        {
            throw new InvalidOperationException("The pool already owns this object.");
        }

        idle.Push(item);
        idleSet.Add(item);
    }

    /// <summary>Hands out an idle object, or creates one when <see cref="AutoExpand"/> and the size allow.</summary>
    /// <param name="item">The object, once handed out.</param>
    /// <returns>False when the pool is exhausted.</returns>
    public bool TryGet([NotNullWhen(true)] out T? item)
    {
        while (idle.TryPop(out var candidate))
        {
            idleSet.Remove(candidate);
            if (isAlive is not null && !isAlive(candidate))
            {
                owned.Remove(candidate);
                continue;
            }

            Leave(candidate);
            item = candidate;
            return true;
        }

        if (AutoExpand && create is not null && CanGrow)
        {
            var created = create();
            owned.Add(created);
            Leave(created);
            item = created;
            return true;
        }

        item = null;
        return false;
    }

    /// <summary>Hands out an object like <see cref="TryGet"/>, failing loudly when the pool is exhausted.</summary>
    /// <returns>The object.</returns>
    public T Get() =>
        TryGet(out var item) ? item : throw new InvalidOperationException($"The pool is exhausted at {MaxSize} objects.");

    /// <summary>Returns an object to the pool; a dead object is dropped instead.</summary>
    /// <param name="item">An object this pool handed out.</param>
    public void Release(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!owned.Contains(item))
        {
            throw new ArgumentException("The object does not belong to this pool.", nameof(item));
        }

        if (idleSet.Contains(item))
        {
            throw new InvalidOperationException("The object was already released.");
        }

        if (isAlive is not null && !isAlive(item))
        {
            owned.Remove(item);
            return;
        }

        if (item is IPoolable poolable)
        {
            poolable.OnReleaseToPool();
        }

        onRelease?.Invoke(item);
        idle.Push(item);
        idleSet.Add(item);
    }

    /// <summary>Discards every idle object; objects handed out stay owned until released.</summary>
    public void Clear()
    {
        while (idle.TryPop(out var item))
        {
            idleSet.Remove(item);
            owned.Remove(item);
            onDispose?.Invoke(item);
        }
    }

    void Leave(T item)
    {
        onGet?.Invoke(item);
        if (item is IPoolable poolable)
        {
            poolable.OnGetFromPool();
        }
    }
}
