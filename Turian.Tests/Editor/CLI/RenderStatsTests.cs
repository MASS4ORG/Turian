using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Tests the render summary headless play prints.</summary>
public class RenderStatsTests
{
    /// <summary>The human-readable summary uses frame wall times and reports the most recent allocation.</summary>
    [Fact]
    public void SummaryReportsFrameTimeAndAllocation()
    {
        var stats = new RenderStats();
        stats.AddFrame(10, 1024, null);
        stats.AddFrame(20, 1536, null);
        stats.AddFrame(30, 2048, null);
        var logger = new SummaryLogger();
        stats.Report(logger);
        Assert.Contains("Frame CPU + wait", logger.Message);
        Assert.Contains("20.00 ms over 3 frames", logger.Message);
        Assert.Contains("2.0 KiB", logger.Message);
    }

    /// <summary>JSON preserves wall timings and geometry and marks unavailable GPU measurements as null.</summary>
    [Fact]
    public void JsonReportPreservesMeasurementsAndUnavailableMetrics()
    {
        var directory = Directory.CreateTempSubdirectory("turian-frame-report-");
        try
        {
            var stats = new RenderStats();
            stats.AddFrame(25, 1024, new RenderFrameStats { DrawCalls = 2, Triangles = 12, CpuMilliseconds = 20 });
            stats.AddFrame(75, 2048, null);
            var path = Path.Combine(directory.FullName, "reports", "stats.json");
            stats.WriteJson(path, 125, new AssetLoadStats(1, 2, 10, 20));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var report = document.RootElement;
            Assert.Equal(2, report.GetProperty("frames").GetInt32());
            Assert.Equal(50, report.GetProperty("averageFrameMilliseconds").GetDouble());
            Assert.Equal(20, report.GetProperty("measuredFramesPerSecond").GetDouble());
            Assert.Equal(125, report.GetProperty("sceneLoadMilliseconds").GetDouble());
            Assert.Equal(20, report.GetProperty("assetLoads").GetProperty("textureMilliseconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("gpuMilliseconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("gpuBufferBytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("gpuTextureBytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("gpuRenderTargetBytes").ValueKind);
            var samples = report.GetProperty("samples");
            Assert.Equal(12, samples[0].GetProperty("rendering").GetProperty("triangles").GetInt64());
            Assert.Equal(JsonValueKind.Null, samples[1].GetProperty("rendering").ValueKind);
            new RenderStats().WriteJson(path, null, default);
            using var empty = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(0, empty.RootElement.GetProperty("measuredFramesPerSecond").GetDouble());
        }
        finally
        {
            directory.Delete(true);
        }
    }

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

    sealed class SummaryLogger : ILogger
    {
        /// <summary>The formatted summary emitted by the reporter.</summary>
        public string Message { get; private set; } = string.Empty;

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Message = formatter(state, exception);
    }
}
