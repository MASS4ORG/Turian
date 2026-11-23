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

    /// <summary>Scheduled work carries its collector to the worker after the scheduling frame ends.</summary>
    [Fact]
    public async Task ScheduledWorkUsesItsOriginatingSink()
    {
        const string name = "Load Asset";
        Assert.False(Profiler.ScheduleWork(name).IsScheduled);
        var sink = Substitute.For<ICpuWorkSink>();
        sink.IsRecording.Returns(true);
        sink.ScheduleWork(name).Returns(41L);
        sink.TryBeginWork(41).Returns(true);

        Assert.True(Profiler.TryAttach(sink));
        Profiler.ScheduledWork work;
        try
        {
            work = Profiler.ScheduleWork(name);
            Assert.True(work.IsScheduled);
        }
        finally
        {
            Assert.True(Profiler.TryDetach(sink));
        }

        await Task.Run(() => { using var interval = work.Begin(); }, TestContext.Current.CancellationToken);

        sink.Received(1).ScheduleWork(name);
        sink.Received(1).TryBeginWork(41);
        sink.Received(1).EndWork(41);
    }
}
