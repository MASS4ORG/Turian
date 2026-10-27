namespace Turian.Engine.Core;

/// <summary>Owns gameplay asset state for one Play/export session; dispose on the game-loop thread to reset every asset.</summary>
public sealed class DataAssetSession : IDisposable
{
    readonly int threadId = Environment.CurrentManagedThreadId;
    readonly Dictionary<Guid, DataAsset> assets = [];
    readonly Dictionary<Guid, object> states = [];
    readonly Lock gate = new();
    readonly DataAssetEventBus bus;
    bool disposed;

    /// <summary>Creates a session on its game-loop thread, with a bounded preallocated queue per typed topic.</summary>
    public DataAssetSession(int queueCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        bus = new DataAssetEventBus(queueCapacity, EnsureMainThread);
    }

    internal IDataAssetEventBus Bus => bus;

    /// <summary>Optional Studio tracing sink; installing it does not box payloads or allocate during raises.</summary>
    public IDataAssetEventObserver? Observer
    {
        get => bus.Observer;
        set { EnsureMainThread(); bus.Observer = value; }
    }

    /// <summary>Attaches a hydrated asset to this session; loader workers may attach before scene activation.</summary>
    public void Attach(DataAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (asset.Id == Guid.Empty) { throw new InvalidOperationException("A DataAsset requires a nonempty GUID."); }
            if (asset.RuntimeSession is { } previous && !ReferenceEquals(previous, this))
            {
                throw new InvalidOperationException("A DataAsset cannot belong to two runtime sessions.");
            }
            if (assets.TryGetValue(asset.Id, out var existing) && !ReferenceEquals(existing, asset))
            {
                throw new InvalidOperationException($"Two runtime DataAssets share identity {asset.Id}.");
            }
            assets[asset.Id] = asset;
            asset.RuntimeSession = this;
            asset.InitializeRuntime(this);
        }
    }

    /// <summary>Finds an already hydrated runtime asset by GUID without I/O or authored-asset fallback.</summary>
    public DataAsset? Find(Guid id)
    {
        lock (gate) { return assets.GetValueOrDefault(id); }
    }

    /// <summary>Returns the currently hydrated runtime assets for editor inspection.</summary>
    public IReadOnlyList<DataAsset> Assets
    {
        get { lock (gate) { return assets.Values.ToArray(); } }
    }

    /// <summary>Lists active listeners in delivery order: larger priority first, ties by subscription order.</summary>
    public IReadOnlyList<DataAssetSubscription> Listeners(Guid topic) => bus.Listeners(topic);

    /// <summary>Delivers worker raises after fixed-tick systems finish; never call from a worker callback.</summary>
    public void Drain() => bus.Drain();

    internal TState State<TState>(DataAsset asset, Func<DataAsset, TState> create) where TState : class
    {
        EnsureMainThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!states.TryGetValue(asset.Id, out var state)) { states.Add(asset.Id, state = create(asset)); }
        return (TState)state;
    }

    internal void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("DataAsset gameplay dispatch requires the game-loop thread; use TryQueue from workers.");
        }
    }

    /// <summary>Releases listeners, pending worker payloads and runtime values, and detaches all hydrated assets.</summary>
    public void Dispose()
    {
        EnsureMainThread();
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            bus.Dispose();
            foreach (var asset in assets.Values)
            {
                asset.ResetRuntime();
                asset.RuntimeSession = null;
            }
            foreach (var state in states.Values)
            {
                (state as IDisposable)?.Dispose();
            }
            states.Clear();
            assets.Clear();
        }
    }
}
