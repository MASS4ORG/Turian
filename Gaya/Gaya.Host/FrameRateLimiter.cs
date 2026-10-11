namespace Gaya.Host;

/// <summary>Paces desktop frames against a monotonic clock without delaying uncapped rendering.</summary>
public sealed class FrameRateLimiter
{
    readonly FrameRateSettings settings;
    readonly Func<long> timestamp;
    readonly long frequency;
    readonly Action<TimeSpan> delay;
    long lastFrame;
    bool started;

    /// <summary>Whether the live performance preferences enable on-demand rendering.</summary>
    public bool RenderOnDemand => settings.RenderOnDemand;

    /// <summary>Creates a limiter that reads live preferences and sleeps between frames.</summary>
    public FrameRateLimiter(FrameRateSettings settings)
        : this(settings, Stopwatch.GetTimestamp, Stopwatch.Frequency,
            duration => Thread.Sleep((int)Math.Ceiling(duration.TotalMilliseconds)))
    { }

    internal FrameRateLimiter(FrameRateSettings settings, Func<long> timestamp, long frequency, Action<TimeSpan> delay)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.timestamp = timestamp;
        this.frequency = frequency;
        this.delay = delay;
    }

    /// <summary>Waits for the next allowed frame start, accounting for time spent rendering the last one.</summary>
    public void Wait()
    {
        var fps = settings.CapFps;
        if (fps == 0)
        {
            started = false;
            return;
        }
        var now = timestamp();
        var interval = Math.Max(1, frequency / fps);
        var remaining = interval - (now - lastFrame);
        if (started && remaining > 0) delay(TimeSpan.FromSeconds((double)remaining / frequency));
        lastFrame = timestamp();
        started = true;
    }
}
