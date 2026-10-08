namespace Turian.Engine.Core;

/// <summary>Owns allocation-free, opt-in stopwatch scopes for one offscreen render call.</summary>
struct RenderFrameMeasurement
{
    readonly bool enabled;
    readonly long started;
    readonly long allocated;
    long preparation;
    long submission;

    /// <summary>Starts accounting only when the view requests statistics.</summary>
    public RenderFrameMeasurement(bool enabled)
    {
        this.enabled = enabled;
        if (!enabled) return;
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Marks the beginning of scene preparation and uploads.</summary>
    public void BeginPreparation()
    {
        if (enabled) preparation = Stopwatch.GetTimestamp();
    }

    /// <summary>Marks command recording, submission and the GPU completion wait.</summary>
    public void BeginSubmission()
    {
        if (enabled) submission = Stopwatch.GetTimestamp();
    }

    /// <summary>Completes the CPU measurements, retaining the recorded geometry counts.</summary>
    public readonly RenderFrameStats Complete(RenderFrameStats geometry)
    {
        if (!enabled) return default;
        var ended = Stopwatch.GetTimestamp();
        return geometry with
        {
            CpuMilliseconds = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds,
            PrepareMilliseconds = Stopwatch.GetElapsedTime(preparation, submission).TotalMilliseconds,
            SubmitMilliseconds = Stopwatch.GetElapsedTime(submission, ended).TotalMilliseconds,
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
        };
    }
}
