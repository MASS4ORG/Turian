using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Tests the render summary headless play prints.</summary>
public class RenderStatsTests
{
    /// <summary>The median is the middle of the sorted times, and the last allocation wins.</summary>
    [Fact]
    public void MedianAndLastAllocation()
    {
        var stats = new RenderStats();
        stats.Add(9, 100);
        stats.Add(1, 200);
        stats.Add(5, 300);

        Assert.Equal(5, stats.Median());
        Assert.Equal(3, stats.Frames);
        Assert.Equal(300, stats.LastAllocated);
        stats.Report(NullLogger.Instance);
    }

    /// <summary>With no frames there is no median and nothing to report.</summary>
    [Fact]
    public void EmptyStatsReportNothing()
    {
        var stats = new RenderStats();

        Assert.Equal(0, stats.Median());
        stats.Report(NullLogger.Instance);
    }
}
