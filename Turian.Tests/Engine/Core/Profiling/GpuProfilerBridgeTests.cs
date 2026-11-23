namespace Turian.Tests;

/// <summary>Checks GPU submission opt-in and correlation independently of the renderer.</summary>
public sealed class GpuProfilerBridgeTests
{
    /// <summary>Completions after a later attachment retain the original frame and submission number.</summary>
    [Fact]
    public void DeferredCompletionKeepsItsOriginalFrame()
    {
        var sink = new FakeGpuSink();
        Assert.False(Profiler.BeginGpuSubmission().IsActive);
        Assert.True(Profiler.TryAttach(sink));
        try
        {
            Assert.False(Profiler.BeginGpuSubmission().IsActive);

            sink.GpuFrameNumber = 41;
            var captured = Profiler.BeginGpuSubmission();
            Assert.True(captured.IsActive);
            Assert.True(Profiler.TryDetach(sink));

            sink.GpuFrameNumber = 42;
            Assert.True(Profiler.TryAttach(sink));
            var later = Profiler.BeginGpuSubmission();
            Assert.True(later.SubmissionId > captured.SubmissionId);
            captured.Complete(GpuSubmissionStatus.TimestampUnsupported, default,
                new GpuMemoryCounter[] { new("Readback", 400u) });

            Assert.Equal(41, sink.FrameNumber);
            var result = Assert.IsType<GpuSubmission>(sink.Result);
            Assert.Equal(captured.SubmissionId, result.SubmissionId);
            Assert.Equal(GpuSubmissionStatus.TimestampUnsupported, result.Status);
            Assert.Equal(400ul, result.Memory.Span[0].Bytes);
        }
        finally
        {
            Profiler.TryDetach(sink);
        }
    }

    sealed class FakeGpuSink : IGpuProfileSink
    {
        public long GpuFrameNumber { get; set; }
        public long FrameNumber { get; private set; }
        public GpuSubmission? Result { get; private set; }
        public bool IsRecording => true;
        public long BeginSample(string name) => 0;
        public void EndSample(long token) { }
        public void RecordGpuSubmission(long frameNumber, GpuSubmission result)
        {
            FrameNumber = frameNumber;
            Result = result;
        }
    }
}
