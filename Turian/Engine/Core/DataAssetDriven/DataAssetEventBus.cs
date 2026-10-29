using System.Diagnostics.CodeAnalysis;

namespace Turian.Engine.Core;

/// <summary>Resolves typed channels during hydration and owns session teardown, worker draining and tracing.</summary>
sealed class DataAssetEventBus(int queueCapacity, Action ensureMainThread) : IDataAssetEventBus
{
    readonly Dictionary<Guid, IDataAssetEventChannel> topics = [];
    IDataAssetEventChannel[] orderedTopics = [];
    int topicCount;
    readonly Lock gate = new();
    IDataAssetEventObserver? observer;
    long sequence;
    bool disposed;

    // Zero invalidates every cached channel without consulting the identity registry during a raise.
    internal int DispatchThreadId = Environment.CurrentManagedThreadId;

    internal IDataAssetEventObserver? Observer
    {
        get => observer;
        set
        {
            ensureMainThread();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                observer = value;
                for (var i = 0; i < topicCount; i++) { orderedTopics[i].SetObserver(value); }
            }
        }
    }

    public DataAssetEventChannel<T> Prepare<T>(Guid topic)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (topics.TryGetValue(topic, out var existing))
            {
                return existing as DataAssetEventChannel<T> ?? throw new InvalidOperationException(
                    $"DataAsset topic {topic} has a different payload type.");
            }
            var channel = new DataAssetEventChannel<T>(this, topic, queueCapacity);
            channel.SetObserver(observer);
            topics.Add(topic, channel);
            if (topicCount == orderedTopics.Length)
            {
                Array.Resize(ref orderedTopics, Math.Max(4, topicCount * 2));
            }
            orderedTopics[topicCount++] = channel;
            return channel;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long NextSequence() => ++sequence;

    internal void EnsureMainThread() => ensureMainThread();

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void RejectDispatch()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        throw new InvalidOperationException(
            "DataAsset gameplay dispatch requires the game-loop thread; use TryQueue from workers.");
    }

    public void Drain()
    {
        ensureMainThread();
        IDataAssetEventChannel[] snapshot;
        int count;
        lock (gate) { snapshot = orderedTopics; count = topicCount; }
        for (var i = 0; i < count && DispatchThreadId != 0; i++) { snapshot[i].Drain(); }
    }

    public IReadOnlyList<DataAssetSubscription> Listeners(Guid topic)
    {
        ensureMainThread();
        lock (gate) { return topics.TryGetValue(topic, out var channel) ? channel.Listeners() : []; }
    }

    public void Dispose()
    {
        ensureMainThread();
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            Volatile.Write(ref DispatchThreadId, 0);
            observer = null;
            for (var i = 0; i < topicCount; i++) { orderedTopics[i].Dispose(); }
            Array.Clear(orderedTopics);
            orderedTopics = [];
            topicCount = 0;
            topics.Clear();
        }
    }
}
