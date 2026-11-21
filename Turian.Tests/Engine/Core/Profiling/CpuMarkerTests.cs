namespace Turian.Tests;

/// <summary>Verifies the engine marker contract independently of any recording plugin.</summary>
public sealed class CpuMarkerTests
{
    /// <summary>Only the attached sink on the current thread receives markers.</summary>
    [Fact]
    public void MarkersRequireACurrentThreadSink()
    {
        var sink = Substitute.For<ICpuSampleSink>();
        sink.IsRecording.Returns(true);
        sink.BeginSample("Tick").Returns(5L);
        Assert.False(Profiler.IsRecording);
        Assert.True(Profiler.TryAttach(sink));
        try
        {
            using (Profiler.Sample("Tick"))
            {
                var worker = new Thread(static () => { using var marker = Profiler.Sample("Worker"); });
                worker.Start();
                worker.Join();
            }
            sink.Received(1).BeginSample("Tick");
            sink.Received(1).EndSample(5);
            sink.DidNotReceive().BeginSample("Worker");
        }
        finally
        {
            Assert.True(Profiler.TryDetach(sink));
        }
        Assert.False(Profiler.IsRecording);
    }
}
