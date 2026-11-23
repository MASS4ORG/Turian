namespace Turian.Tests;

/// <summary>Checks the bounded development-player capture.</summary>
public sealed class PlayerProfileRecorderTests
{
    /// <summary>Nested markers retain their parent and unfinished markers end at the frame boundary.</summary>
    [Fact]
    public void RecordsNestedAndUnfinishedSamples()
    {
        var recorder = new PlayerProfileRecorder();
        Profiler.SampleScope unfinished;
        using (recorder.BeginFrame())
        {
            using var outer = Profiler.Sample("Outer");
            using (Profiler.Sample("Inner")) { }
            unfinished = Profiler.Sample("Unfinished");
        }
        unfinished.Dispose();

        var frame = Assert.Single(recorder.ReadAfter(0, 10));
        Assert.Equal(1, frame.UnfinishedSamples);
        Assert.Equal(3, frame.Samples.Length);
        Assert.Equal(-1, frame.Samples[0].ParentIndex);
        Assert.Equal(0, frame.Samples[1].ParentIndex);
        Assert.Equal(0, frame.Samples[2].ParentIndex);
        Assert.All(frame.Samples, sample => Assert.InRange(sample.StartTicks, 0, frame.DurationTicks));
        Assert.All(frame.Samples, sample => Assert.InRange(sample.DurationTicks, 0, frame.DurationTicks));
    }

    /// <summary>Sample and name limits bound each published frame.</summary>
    [Fact]
    public void DropsExcessSamplesAndTruncatesNames()
    {
        var recorder = new PlayerProfileRecorder();
        using (recorder.BeginFrame())
        {
            for (var i = 0; i < 70; i++)
            {
                using var sample = Profiler.Sample(new string('x', 100));
            }
        }

        var frame = Assert.Single(recorder.ReadAfter(0, 1));
        Assert.Equal(64, frame.Samples.Length);
        Assert.Equal(6, frame.DroppedSamples);
        Assert.All(frame.Samples, static sample => Assert.Equal(96, sample.Name.Length));
    }

    /// <summary>History rolls over in order and stale frame scopes do not stop a later frame.</summary>
    [Fact]
    public void HistoryIsBoundedAndStaleScopesAreHarmless()
    {
        var recorder = new PlayerProfileRecorder();
        var stale = recorder.BeginFrame();
        stale.Dispose();
        for (var i = 0; i < 125; i++)
        {
            using var frame = recorder.BeginFrame();
            stale.Dispose();
            using var sample = Profiler.Sample("Frame");
        }

        var frames = recorder.ReadAfter(0, 200);
        Assert.Equal(120, frames.Length);
        Assert.Equal(7, frames[0].Number);
        Assert.Equal(126, frames[^1].Number);
        Assert.All(frames, static frame => Assert.Single(frame.Samples));
        Assert.Equal(new long[] { 100, 101 }, recorder.ReadAfter(99, 2).Select(static frame => frame.Number));
        Assert.Empty(recorder.ReadAfter(0, 0));
    }

    /// <summary>A frame belongs to the thread that attached its sink.</summary>
    [Fact]
    public void OtherThreadCannotEndFrame()
    {
        var recorder = new PlayerProfileRecorder();
        var frame = recorder.BeginFrame();
        var thread = new Thread(frame.Dispose);
        thread.Start();
        thread.Join();
        using (Profiler.Sample("Still recording")) { }
        frame.Dispose();

        Assert.Single(Assert.Single(recorder.ReadAfter(0, 1)).Samples);
    }
}
