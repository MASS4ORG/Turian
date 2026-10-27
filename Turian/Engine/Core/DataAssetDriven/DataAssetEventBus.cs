using System.Buffers;

namespace Turian.Engine.Core;

/// <summary>Session-owned typed topics with pooled, immutable listener snapshots and bounded worker queues.</summary>
sealed class DataAssetEventBus(int queueCapacity, Action ensureMainThread) : IDataAssetEventBus
{
    readonly Dictionary<Guid, ITopic> topics = [];
    readonly List<ITopic> orderedTopics = [];
    readonly Lock gate = new();
    long sequence;
    bool disposed;

    internal IDataAssetEventObserver? Observer { get; set; }

    void EnsureMainThread() => ensureMainThread();

    public void Prepare<T>(Guid topic) => _ = GetTopic<T>(topic);

    public IDisposable Subscribe<T>(Guid topic, Action<T> listener, int priority, Guid owner)
    {
        ensureMainThread();
        ArgumentNullException.ThrowIfNull(listener);
        return GetTopic<T>(topic).Subscribe(listener, priority, owner, ++sequence);
    }

    public void Raise<T>(Guid topic, T payload)
    {
        ensureMainThread();
        var target = GetTopic<T>(topic);
        var trace = new DataAssetEventTrace(++sequence, topic, Stopwatch.GetTimestamp(), target.Count);
        Observer?.OnRaised(in trace);
        target.Raise(payload);
    }

    public bool TryQueue<T>(Guid topic, T payload) => GetTopic<T>(topic).TryQueue(payload);

    public void Drain()
    {
        ensureMainThread();
        var count = orderedTopics.Count;
        for (var i = 0; i < count && !disposed; i++)
        {
            orderedTopics[i].Drain();
        }
    }

    public IReadOnlyList<DataAssetSubscription> Listeners(Guid topic)
    {
        ensureMainThread();
        lock (gate)
        {
            return topics.TryGetValue(topic, out var target) ? target.Listeners() : [];
        }
    }

    Topic<T> GetTopic<T>(Guid id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (topics.TryGetValue(id, out var existing))
            {
                return existing as Topic<T> ?? throw new InvalidOperationException(
                    $"DataAsset topic {id} has a different payload type.");
            }

            var created = new Topic<T>(this, id, queueCapacity);
            topics.Add(id, created);
            orderedTopics.Add(created);
            return created;
        }
    }

    public void Dispose()
    {
        ensureMainThread();
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            Observer = null;
            foreach (var topic in orderedTopics) { topic.Dispose(); }
            orderedTopics.Clear();
            topics.Clear();
        }
    }

    interface ITopic : IDisposable
    {
        void Drain();
        IReadOnlyList<DataAssetSubscription> Listeners();
    }

    sealed class Topic<T>(DataAssetEventBus bus, Guid id, int capacity) : ITopic
    {
        Entry?[] listeners = [];
        readonly List<Entry?[]> retired = [];
        readonly T[] queue = new T[capacity];
        readonly Lock queueGate = new();
        int depth;
        int count;
        int head;
        int queued;
        bool ended;

        public int Count => count;

        public IDisposable Subscribe(Action<T> listener, int priority, Guid owner, long sequence)
        {
            var entry = new Entry(this, listener, new DataAssetSubscription(id, owner, priority, sequence));
            var next = ArrayPool<Entry?>.Shared.Rent(count + 1);
            var insertion = 0;
            while (insertion < count && listeners[insertion]!.Info.Priority >= priority) { insertion++; }
            Array.Copy(listeners, next, insertion);
            next[insertion] = entry;
            Array.Copy(listeners, insertion, next, insertion + 1, count - insertion);
            Replace(next);
            count++;
            return entry;
        }

        public void Raise(T payload)
        {
            var snapshot = listeners;
            var length = count;
            depth++;
            try
            {
                for (var i = 0; i < length; i++)
                {
                    var entry = snapshot[i]!;
                    if (entry.Active) { entry.Listener!(payload); }
                }
            }
            finally
            {
                depth--;
                if (depth == 0)
                {
                    foreach (var previous in retired) { Return(previous); }
                    retired.Clear();
                }
            }
        }

        public bool TryQueue(T payload)
        {
            lock (queueGate)
            {
                if (ended || queued == queue.Length) { return false; }
                queue[(head + queued) % queue.Length] = payload;
                queued++;
                return true;
            }
        }

        public void Drain()
        {
            int available;
            lock (queueGate) { available = queued; }
            for (var i = 0; i < available; i++)
            {
                T payload;
                lock (queueGate)
                {
                    if (queued == 0) { break; }
                    payload = queue[head];
                    queue[head] = default!;
                    head = (head + 1) % queue.Length;
                    queued--;
                }
                bus.Raise(id, payload);
            }
        }

        public IReadOnlyList<DataAssetSubscription> Listeners()
        {
            var result = new DataAssetSubscription[count];
            for (var i = 0; i < count; i++) { result[i] = listeners[i]!.Info; }
            return result;
        }

        void Remove(Entry entry)
        {
            bus.EnsureMainThread();
            if (!entry.Active) { return; }
            entry.Active = false;
            entry.Listener = null;
            var next = count > 1 ? ArrayPool<Entry?>.Shared.Rent(count - 1) : [];
            var target = 0;
            for (var i = 0; i < count; i++)
            {
                if (!ReferenceEquals(listeners[i], entry)) { next[target++] = listeners[i]; }
            }
            Replace(next);
            count--;
        }

        void Replace(Entry?[] next)
        {
            var previous = listeners;
            listeners = next;
            if (previous.Length == 0) { return; }
            if (depth == 0) { Return(previous); }
            else { retired.Add(previous); }
        }

        static void Return(Entry?[] value)
        {
            if (value.Length != 0) { ArrayPool<Entry?>.Shared.Return(value, clearArray: true); }
        }

        public void Dispose()
        {
            lock (queueGate)
            {
                ended = true;
                Array.Clear(queue);
                queued = 0;
            }
            for (var i = 0; i < count; i++)
            {
                listeners[i]!.Active = false;
                listeners[i]!.Listener = null;
            }
            Replace([]);
            count = 0;
        }

        sealed class Entry(Topic<T> topic, Action<T> listener, DataAssetSubscription info) : IDisposable
        {
            public readonly DataAssetSubscription Info = info;
            public Action<T>? Listener = listener;
            public bool Active = true;
            public void Dispose() => topic.Remove(this);
        }
    }
}
