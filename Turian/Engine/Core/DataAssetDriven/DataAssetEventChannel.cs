namespace Turian.Engine.Core;

/// <summary>Session-owned channel operations used by the hydration registry, independent of payload type.</summary>
interface IDataAssetEventChannel : IDisposable
{
    void Drain();
    void SetObserver(IDataAssetEventObserver? observer);
    IReadOnlyList<DataAssetSubscription> Listeners();
}

/// <summary>Instance-held delivery state with immutable snapshots, tombstoned subscriptions and a bounded worker queue.</summary>
sealed class DataAssetEventChannel<T>(DataAssetEventBus bus, Guid id, int capacity) : IDataAssetEventChannel
{
    Entry[] listeners = [];
    readonly T[] queue = new T[capacity];
    readonly Lock queueGate = new();
    IDataAssetEventObserver? observer;
    int head;
    int queued;
    bool ended;

    public void SetObserver(IDataAssetEventObserver? value) => observer = value;

    /// <summary>Publishes on the owning thread; disposal and thread violations share one cold rejection branch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Raise(T payload)
    {
        if (bus.DispatchThreadId != Environment.CurrentManagedThreadId) { bus.RejectDispatch(); }
        var sequence = bus.NextSequence();
        var sink = observer;
        if (sink is not null) { Trace(sink, sequence); }
        var snapshot = Volatile.Read(ref listeners);
        for (var i = 0; i < snapshot.Length; i++)
        {
            var callback = Volatile.Read(ref snapshot[i].Callback);
            callback?.Invoke(payload);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Trace(IDataAssetEventObserver sink, long sequence)
    {
        var trace = new DataAssetEventTrace(sequence, id, Stopwatch.GetTimestamp(), listeners.Length);
        sink.OnRaised(in trace);
    }

    internal IDisposable Subscribe(Action<T> listener, int priority, Guid owner)
    {
        if (bus.DispatchThreadId != Environment.CurrentManagedThreadId) { bus.RejectDispatch(); }
        ArgumentNullException.ThrowIfNull(listener);
        var entry = new Entry(this, listener, new DataAssetSubscription(id, owner, priority, bus.NextSequence()));
        var snapshot = listeners;
        var insertion = 0;
        while (insertion < snapshot.Length && snapshot[insertion].Info.Priority >= priority) { insertion++; }
        var next = new Entry[snapshot.Length + 1];
        Array.Copy(snapshot, next, insertion);
        next[insertion] = entry;
        Array.Copy(snapshot, insertion, next, insertion + 1, snapshot.Length - insertion);
        Volatile.Write(ref listeners, next);
        return entry;
    }

    void Remove(Entry entry)
    {
        bus.EnsureMainThread();
        if (entry.Callback is null) { return; }
        Volatile.Write(ref entry.Callback, null);
        var snapshot = listeners;
        var index = Array.IndexOf(snapshot, entry);
        var next = snapshot.Length == 1 ? [] : new Entry[snapshot.Length - 1];
        Array.Copy(snapshot, next, index);
        Array.Copy(snapshot, index + 1, next, index, snapshot.Length - index - 1);
        Volatile.Write(ref listeners, next);
    }

    internal bool TryQueue(T payload)
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
            Raise(payload);
        }
    }

    public IReadOnlyList<DataAssetSubscription> Listeners()
    {
        var snapshot = listeners;
        var result = new DataAssetSubscription[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++) { result[i] = snapshot[i].Info; }
        return result;
    }

    public void Dispose()
    {
        lock (queueGate)
        {
            ended = true;
            Array.Clear(queue);
            queued = 0;
        }
        observer = null;
        var snapshot = Interlocked.Exchange(ref listeners, []);
        foreach (var entry in snapshot) { Volatile.Write(ref entry.Callback, null); }
    }

    sealed class Entry(DataAssetEventChannel<T> channel, Action<T> callback, DataAssetSubscription info) : IDisposable
    {
        internal readonly DataAssetSubscription Info = info;
        internal Action<T>? Callback = callback;
        public void Dispose() => channel.Remove(this);
    }
}
