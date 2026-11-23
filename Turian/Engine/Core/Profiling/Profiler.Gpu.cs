namespace Turian.Engine.Core;

public static partial class Profiler
{
    static long nextGpuSubmissionId;

    /// <summary>Captures the current profiler frame and a submission ID before recording GPU commands.</summary>
    public static GpuCapture BeginGpuSubmission()
    {
        if (current is IGpuProfileSink { IsRecording: true } sink && sink.GpuFrameNumber is > 0 and var frame)
        {
            return new GpuCapture(sink, frame, Interlocked.Increment(ref nextGpuSubmissionId));
        }
        return default;
    }

    /// <summary>Links GPU results to the frame that originally recorded the commands.</summary>
    public readonly struct GpuCapture
    {
        readonly IGpuProfileSink? sink;
        readonly long frameNumber;

        internal GpuCapture(IGpuProfileSink sink, long frameNumber, long submissionId)
        {
            this.sink = sink;
            this.frameNumber = frameNumber;
            SubmissionId = submissionId;
        }

        /// <summary>The process-local identity of the submission, or zero when GPU capture is idle.</summary>
        public long SubmissionId { get; }

        /// <summary>Whether this submission has a recording destination.</summary>
        public bool IsActive => sink is not null;

        /// <summary>Publishes one result to the originating frame, even after its attachment ends.</summary>
        public void Complete(GpuSubmissionStatus status, ReadOnlyMemory<GpuPassTiming> passes,
            ReadOnlyMemory<GpuMemoryCounter> memory)
        {
            sink?.RecordGpuSubmission(frameNumber, new GpuSubmission(SubmissionId, status, passes, memory));
        }
    }
}

/// <summary>Receives optional render submissions correlated with a recording frame.</summary>
public interface IGpuProfileSink : ICpuSampleSink
{
    /// <summary>The attached frame number, or zero when GPU capture is disabled.</summary>
    long GpuFrameNumber { get; }

    /// <summary>Stores a result for the originating frame, dropping stale or duplicate submissions.</summary>
    void RecordGpuSubmission(long frameNumber, GpuSubmission result);
}

/// <summary>Whether timing data is available for a recorded GPU submission.</summary>
public enum GpuSubmissionStatus
{
    /// <summary>The submission has valid timing samples.</summary>
    Complete,
    /// <summary>The render queue does not support timestamps.</summary>
    TimestampUnsupported,
    /// <summary>Valid timing samples were unavailable for this submission.</summary>
    Unavailable
}

/// <summary>A pass measured on the GPU clock relative to the start of its submission.</summary>
/// <param name="Name">Render pass or command phase.</param>
/// <param name="StartMilliseconds">Elapsed GPU time from the first submission timestamp.</param>
/// <param name="DurationMilliseconds">Elapsed GPU time between the phase's boundaries.</param>
public readonly record struct GpuPassTiming(string Name, double StartMilliseconds, double DurationMilliseconds);

/// <summary>Bytes reported for one graphics allocation category.</summary>
/// <param name="Name">Category label, including whether it is estimated.</param>
/// <param name="Bytes">Current allocated or estimated bytes for the category.</param>
public readonly record struct GpuMemoryCounter(string Name, ulong Bytes);

/// <summary>Results from one GPU submission. Timestamp and memory buffers are immutable after publication.</summary>
/// <param name="SubmissionId">Process-local submission identity.</param>
/// <param name="Status">Timestamp availability for this submission.</param>
/// <param name="Passes">Clock-relative timings, when available.</param>
/// <param name="Memory">Tracked graphics allocation categories for this submission.</param>
public sealed record GpuSubmission(long SubmissionId, GpuSubmissionStatus Status,
    ReadOnlyMemory<GpuPassTiming> Passes, ReadOnlyMemory<GpuMemoryCounter> Memory);
