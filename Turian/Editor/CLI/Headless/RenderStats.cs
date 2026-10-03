namespace Turian.Editor.CLI;

/// <summary>Per-frame render timings and the last frame's managed allocations, for headless play's summary.</summary>
public sealed class RenderStats
{
    readonly List<double> times = [];

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

    /// <summary>Gets the median render time, or zero when nothing was recorded.</summary>
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
            "Render: median {Median:F2} ms over {Frames} frames, last frame allocated {Allocated:F1} KiB",
            Median(),
            Frames,
            LastAllocated / 1024.0);
    }
}
