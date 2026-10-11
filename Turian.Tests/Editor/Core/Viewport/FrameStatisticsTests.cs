namespace Turian.Tests;

/// <summary>Checks frame cadence, rolling samples and allocation-free HUD formatting.</summary>
public sealed class FrameStatisticsTests
{
    /// <summary>Rendered frames have real cadence and geometry even when simulation time is paused.</summary>
    [Fact]
    public void WallTimeSamplingResetsAndDoesNotAllocate()
    {
        var statistics = new FrameStatistics();
        var sample = new RenderFrameStats { CpuMilliseconds = 2, DrawCalls = 4, Triangles = 12 };
        statistics.Add(sample);
        Assert.Equal(0, statistics.Count);
        statistics.Add(sample);
        Assert.Equal(1, statistics.Count);
        Assert.True(statistics.FrameMilliseconds > 0);
        Assert.Equal(2, statistics.CpuMilliseconds);
        Span<char> buffer = stackalloc char[192];
        var length = statistics.FormatLine(2, buffer, null, 0);
        Assert.Equal("Draws: 4  |  Submitted tris: 12", new string(buffer[..length]));
        statistics.Clear();
        statistics.Add(sample);
        Assert.Equal(0, statistics.Count);
        for (var i = 0; i < 100; i++) statistics.Add(sample);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) statistics.Add(sample);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(60, statistics.Count);
    }

    /// <summary>FPS uses actual frame intervals and old spikes leave the fixed sampling window.</summary>
    [Fact]
    public void RollingWindowUsesActualCadenceAndResets()
    {
        var stats = new FrameStatistics();
        Assert.Equal(0, stats.FramesPerSecond);
        stats.Add(double.NaN, default);
        stats.Add(double.PositiveInfinity, default);
        stats.Add(0, default);
        stats.Add(-1, default);
        Assert.Equal(0, stats.Count);
        stats.Add(0.04, new RenderFrameStats { CpuMilliseconds = 2 });
        stats.Add(0.02, new RenderFrameStats { CpuMilliseconds = 4 });
        Assert.Equal(30, stats.FrameMilliseconds, 6);
        Assert.Equal(1000.0 / 30, stats.FramesPerSecond, 6);
        Assert.Equal(3, stats.CpuMilliseconds);
        for (var i = 0; i < 60; i++) stats.Add(0.05, new RenderFrameStats { CpuMilliseconds = 5 });
        Assert.Equal(60, stats.Count);
        Assert.Equal(20, stats.FramesPerSecond, 6);
        Assert.Equal(5, stats.CpuMilliseconds);
        stats.Clear();
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.FrameMilliseconds);
        Assert.Equal(0, stats.CpuMilliseconds);
    }

    /// <summary>HUD numbers use invariant formatting and caller storage without steady-state allocations.</summary>
    [Fact]
    public void HudFormattingUsesRollingGeometryWithoutAllocations()
    {
        var stats = new FrameStatistics();
        var sample = new RenderFrameStats
        {
            CpuMilliseconds = 2,
            DrawCalls = 4,
            Triangles = 12,
            MaterialBinds = 2,
            AllocatedBytes = 2048,
            Submeshes = new RenderCullingStats(4, 8),
        };
        stats.Add(0.05, sample);
        stats.Add(0.05, sample with { DrawCalls = 8, Triangles = 24 });
        Span<char> buffer = stackalloc char[192];
        Assert.Equal("20.0 fps  |  50.00 ms/frame", Format(0));
        Assert.Equal("Render CPU + wait: 2.00 ms", Format(1));
        Assert.Equal("Draws: 6  |  Submitted tris: 18", Format(2));
        Assert.Equal("Submeshes: 6 submitted / 8 CPU culled", Format(3));
        Assert.Equal("Material binds: 2  |  Alloc: 2.0 KiB", Format(4));
        Assert.Equal("Scene load: 123.4 ms  |  Texture payload: 1.0 MiB", Format(5));
        var length = stats.FormatLine(5, buffer, null, 1048576);
        Assert.Equal("Texture payload: 1.0 MiB", new string(buffer[..length]));
        Assert.Equal(0, stats.FormatLine(0, [], null, 0));
        Assert.Equal(0, stats.FormatLine(2, [], null, 0));
        for (var i = 0; i < 100; i++) stats.FormatLine(i % 6, buffer, 123.4, 1048576);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            stats.Add(0.05, sample);
            stats.FormatLine(i % 6, buffer, 123.4, 1048576);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);

        string Format(int line)
        {
            Span<char> storage = stackalloc char[192];
            var count = stats.FormatLine(line, storage, 123.4, 1048576);
            return new string(storage[..count]);
        }
    }
}
