namespace Turian.Engine.Core;

/// <summary>Records bounded CPU frames on the player simulation thread.</summary>
sealed class PlayerProfileRecorder : ICpuSampleSink
{
    const int HistoryCapacity = 120;
    const int SampleCapacity = 64;
    const int NameLimit = 96;

    readonly object gate = new();
    readonly PlayerProfileFrame?[] history = new PlayerProfileFrame[HistoryCapacity];
    readonly Entry[] entries = new Entry[SampleCapacity];
    long nextFrame;
    long publishedFrame;
    long nextToken;
    long activeFrame;
    long startTimestamp;
    long startAllocated;
    int startGen0;
    int startGen1;
    int startGen2;
    int ownerThread;
    int count;
    int current = -1;
    int dropped;

    /// <inheritdoc />
    public bool IsRecording => activeFrame != 0 && ownerThread == Environment.CurrentManagedThreadId;

    internal FrameScope BeginFrame()
    {
        lock (gate)
        {
            if (activeFrame != 0 || !Profiler.TryAttach(this))
            {
                return default;
            }

            activeFrame = ++nextFrame;
            ownerThread = Environment.CurrentManagedThreadId;
            count = 0;
            current = -1;
            dropped = 0;
            startAllocated = GC.GetAllocatedBytesForCurrentThread();
            startGen0 = GC.CollectionCount(0);
            startGen1 = GC.CollectionCount(1);
            startGen2 = GC.CollectionCount(2);
            startTimestamp = Stopwatch.GetTimestamp();
            return new FrameScope(this, activeFrame);
        }
    }

    /// <inheritdoc />
    public long BeginSample(string name)
    {
        if (!IsRecording)
        {
            return 0;
        }
        if (count == SampleCapacity || string.IsNullOrEmpty(name))
        {
            dropped++;
            return 0;
        }

        if (name.Length > NameLimit)
        {
            name = name[..NameLimit];
        }
        var token = ++nextToken;
        entries[count] = new Entry(name, current, Stopwatch.GetTimestamp());
        current = count++;
        return token;
    }

    /// <inheritdoc />
    public void EndSample(long token)
    {
        if (!IsRecording || token == 0)
        {
            return;
        }
        var distance = nextToken - token;
        if (distance < 0 || distance >= count)
        {
            return;
        }
        ref var entry = ref entries[count - 1 - (int)distance];
        if (entry.Finished)
        {
            return;
        }
        entry.Duration = Stopwatch.GetTimestamp() - entry.Start;
        entry.Finished = true;
        while (current >= 0 && entries[current].Finished)
        {
            current = entries[current].Parent;
        }
    }

    internal PlayerProfileFrame[] ReadAfter(long afterFrame, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }
        lock (gate)
        {
            if (afterFrame >= publishedFrame || publishedFrame == 0)
            {
                return [];
            }
            var first = Math.Max(Math.Max(1, publishedFrame - HistoryCapacity + 1), afterFrame + 1);
            var frames = new PlayerProfileFrame[(int)Math.Min(limit, publishedFrame - first + 1)];
            for (var i = 0; i < frames.Length; i++)
            {
                frames[i] = history[(int)((first + i - 1) % HistoryCapacity)]!;
            }
            return frames;
        }
    }

    void EndFrame(long frame)
    {
        lock (gate)
        {
            if (frame != activeFrame || !IsRecording)
            {
                return;
            }
            var end = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - startAllocated;
            var managed = GC.GetTotalMemory(false);
            var gen0 = GC.CollectionCount(0) - startGen0;
            var gen1 = GC.CollectionCount(1) - startGen1;
            var gen2 = GC.CollectionCount(2) - startGen2;
            var samples = new PlayerProfileSample[count];
            var unfinished = 0;
            for (var i = 0; i < count; i++)
            {
                ref var entry = ref entries[i];
                if (!entry.Finished)
                {
                    unfinished++;
                }
                samples[i] = new PlayerProfileSample(entry.Name, entry.Parent, entry.Start - startTimestamp,
                    entry.Finished ? entry.Duration : end - entry.Start);
                entry = default;
            }
            var completed = new PlayerProfileFrame(frame, startTimestamp, end - startTimestamp, dropped, unfinished,
                allocated, managed, gen0, gen1, gen2, samples);
            history[(int)((frame - 1) % HistoryCapacity)] = completed;
            publishedFrame = frame;
            activeFrame = 0;
            ownerThread = 0;
            Profiler.TryDetach(this);
        }
    }

    internal readonly struct FrameScope(PlayerProfileRecorder recorder, long frame) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => recorder?.EndFrame(frame);
    }

    struct Entry(string name, int parent, long start)
    {
        internal string Name = name;
        internal int Parent = parent;
        internal long Start = start;
        internal long Duration;
        internal bool Finished;
    }
}
