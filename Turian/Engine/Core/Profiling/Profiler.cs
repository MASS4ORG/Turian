namespace Turian.Engine.Core;

/// <summary>Forwards synchronous CPU markers to the collector attached to the current thread.</summary>
public static partial class Profiler
{
    [ThreadStatic] static ICpuSampleSink? current;

    /// <summary>Whether the current thread accepts markers.</summary>
    public static bool IsRecording => current?.IsRecording == true;

    /// <summary>Begins a named marker. Use a constant name in frequently executed code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SampleScope Sample(string name)
    {
        var sink = current;
        if (sink is null || !sink.IsRecording)
        {
            return default;
        }

        var token = sink.BeginSample(name);
        return token != 0 ? new SampleScope(sink, token) : default;
    }

    /// <summary>Correlates a scheduled operation with the frame and marker that queued it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ScheduledWork ScheduleWork(string name)
    {
        if (current is not ICpuWorkSink { IsRecording: true } sink)
        {
            return default;
        }
        var token = sink.ScheduleWork(name);
        return token != 0 ? new ScheduledWork(sink, token) : default;
    }

    /// <summary>Attaches a collector if the current thread does not already have one.</summary>
    public static bool TryAttach(ICpuSampleSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
#if TURIAN_NO_CPU_MARKERS
        return false;
#else
        if (current is not null)
        {
            return false;
        }

        current = sink;
        return true;
#endif
    }

    /// <summary>Detaches this collector from its recording thread and returns whether it was attached.</summary>
    public static bool TryDetach(ICpuSampleSink sink)
    {
        if (current is null || !ReferenceEquals(current, sink))
        {
            return false;
        }

        current = null;
        return true;
    }

    /// <summary>Closes one sample on its recording thread.</summary>
    public readonly struct SampleScope : IDisposable
    {
        readonly ICpuSampleSink? sink;
        readonly long token;

        internal SampleScope(ICpuSampleSink sink, long token)
        {
            this.sink = sink;
            this.token = token;
        }

        /// <summary>Finishes this marker if its collector is attached to the current thread.</summary>
        public void Dispose()
        {
            if (sink is not null && ReferenceEquals(current, sink))
            {
                sink.EndSample(token);
            }
        }
    }

    /// <summary>Transfers the origin of one queued operation to the thread that executes it.</summary>
    public readonly struct ScheduledWork
    {
        readonly ICpuWorkSink? sink;
        readonly long token;

        internal ScheduledWork(ICpuWorkSink sink, long token)
        {
            this.sink = sink;
            this.token = token;
        }

        /// <summary>Whether the collector accepted the operation when it was scheduled.</summary>
        public bool IsScheduled => sink is not null;

        /// <summary>Starts timing on the worker thread. Dispose the scope on that thread.</summary>
        public WorkScope Begin() => sink is not null && sink.TryBeginWork(token) ? new WorkScope(sink, token) : default;
    }

    /// <summary>Finishes a scheduled operation on the worker thread that began it.</summary>
    public readonly struct WorkScope : IDisposable
    {
        readonly ICpuWorkSink? sink;
        readonly long token;

        internal WorkScope(ICpuWorkSink sink, long token)
        {
            this.sink = sink;
            this.token = token;
        }

        /// <summary>Records completion when the collector still owns the operation.</summary>
        public void Dispose() => sink?.EndWork(token);
    }
}

/// <summary>Receives synchronous CPU markers on an attached collector thread.</summary>
public interface ICpuSampleSink
{
    /// <summary>Whether the collector accepts markers for the current attachment.</summary>
    bool IsRecording { get; }

    /// <summary>Opens a marker and returns a token, or zero if the marker was omitted.</summary>
    long BeginSample(string name);

    /// <summary>Finishes a marker. Stale or repeated tokens must be harmless.</summary>
    void EndSample(long token);
}

/// <summary>Optionally correlates work on other threads with the current recording frame.</summary>
public interface ICpuWorkSink : ICpuSampleSink
{
    /// <summary>Captures the current origin and returns one nonzero token, or zero when omitted.</summary>
    long ScheduleWork(string name);

    /// <summary>Starts timing one scheduled token. Stale or repeated starts must be harmless.</summary>
    bool TryBeginWork(long token);

    /// <summary>Finishes the work on its execution thread. Stale or repeated completions must be harmless.</summary>
    void EndWork(long token);
}
