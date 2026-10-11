namespace Turian.Editor.CLI;

/// <summary>Actual headless frame timings and allocations, with optional submitted geometry samples.</summary>
public sealed class RenderStats
{
    readonly List<double> times = [];
    readonly List<FrameSample> samples = [];

    /// <summary>Gets the number of frames recorded.</summary>
    public int Frames => times.Count;

    /// <summary>Gets the bytes the most recently recorded frame allocated.</summary>
    public long LastAllocated { get; private set; }

    /// <summary>Records one rendered frame.</summary>
    /// <param name="milliseconds">How long the frame took to render.</param>
    /// <param name="allocated">The managed bytes the frame allocated.</param>
    public void Add(double milliseconds, long allocated)
    {
        times.Add(milliseconds);
        LastAllocated = allocated;
    }

    /// <summary>Records actual headless frame wall time, submitted geometry and render CPU scopes.</summary>
    public void AddFrame(double milliseconds, long allocated, RenderFrameStats? rendering)
    {
        Add(milliseconds, allocated);
        samples.Add(new FrameSample(milliseconds, allocated, rendering));
    }

    /// <summary>Writes frame samples and load durations as JSON; unsupported GPU metrics are null.</summary>
    public void WriteJson(string path, double? sceneLoadMilliseconds, AssetLoadStats assetLoads)
    {
        var report = new
        {
            schemaVersion = 1,
            measurement = "Headless tick + offscreen render wall time; uncapped, includes GPU completion wait",
            sceneLoadMilliseconds,
            assetLoads,
            frames = Frames,
            averageFrameMilliseconds = Frames == 0 ? 0 : times.Average(),
            medianFrameMilliseconds = Median(),
            measuredFramesPerSecond = times.Sum() > 0 ? Frames * 1000 / times.Sum() : 0,
            texturePayloadBytes = TextureAsset.UploadedBytes,
            gpuMilliseconds = (double?)null,
            gpuBufferBytes = (ulong?)null,
            gpuTextureBytes = (ulong?)null,
            gpuRenderTargetBytes = (ulong?)null,
            samples,
        };
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    /// <summary>Gets the median frame time, or zero when nothing was recorded.</summary>
    public double Median()
    {
        if (times.Count == 0) return 0;
        var sorted = times.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>Logs the summary line; does nothing when no frame was rendered.</summary>
    /// <param name="logger">The logger to report through.</param>
    public void Report(ILogger logger)
    {
        if (Frames == 0 || !logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation(
            "Frame CPU + wait: median {Median:F2} ms over {Frames} frames, last frame allocated {Allocated:F1} KiB",
            Median(),
            Frames,
            LastAllocated / 1024.0);
    }

    readonly record struct FrameSample(double FrameMilliseconds, long AllocatedBytes, RenderFrameStats? Rendering);
}
