namespace Turian.Engine.Core;

/// <summary>Maintains rolling frame samples without allocating during collection or formatting.</summary>
public sealed class FrameStatistics
{
    readonly (double Interval, RenderFrameStats Stats)[] samples = new (double, RenderFrameStats)[60];
    int next;
    int count;
    long lastTimestamp;

    /// <summary>The number of samples currently included in the rolling average.</summary>
    public int Count => count;

    /// <summary>Mean actual frame interval in milliseconds; including host frame pacing.</summary>
    public double FrameMilliseconds { get; private set; }

    /// <summary>Frames per second derived from the mean actual frame interval.</summary>
    public double FramesPerSecond => FrameMilliseconds > 0 ? 1000 / FrameMilliseconds : 0;

    /// <summary>Mean render-call CPU duration in milliseconds.</summary>
    public double CpuMilliseconds { get; private set; }

    /// <summary>Samples completed renders using wall time, independently of the simulation clock.</summary>
    public void Add(RenderFrameStats stats)
    {
        var now = Stopwatch.GetTimestamp();
        if (lastTimestamp != 0) Add((now - lastTimestamp) / (double)Stopwatch.Frequency, stats);
        lastTimestamp = now;
    }

    /// <summary>Adds a completed render and the actual frame interval, in seconds.</summary>
    public void Add(double intervalSeconds, RenderFrameStats stats)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0) return;
        samples[next] = (intervalSeconds * 1000, stats);
        next = (next + 1) % samples.Length;
        count = Math.Min(count + 1, samples.Length);
        double interval = 0;
        double cpu = 0;
        for (var i = 0; i < count; i++)
        {
            interval += samples[i].Interval;
            cpu += samples[i].Stats.CpuMilliseconds;
        }
        FrameMilliseconds = interval / count;
        CpuMilliseconds = cpu / count;
    }

    /// <summary>Clears samples when statistics are disabled or the displayed scene changes.</summary>
    public void Clear()
    {
        next = count = 0;
        lastTimestamp = 0;
        FrameMilliseconds = CpuMilliseconds = 0;
    }

    /// <summary>Formats one HUD line using invariant BCL formatting and caller-owned storage.</summary>
    public int FormatLine(int line, Span<char> destination, double? sceneLoadMilliseconds, ulong textureBytes)
    {
        var stats = Average();
        var writer = new StatisticsText(destination);
        switch (line)
        {
            case 0:
                writer.Append(FramesPerSecond, "F1");
                writer.Append(" fps  |  ");
                writer.Append(FrameMilliseconds, "F2");
                writer.Append(" ms/frame");
                break;
            case 1:
                writer.Append("Render CPU + wait: ");
                writer.Append(CpuMilliseconds, "F2");
                writer.Append(" ms");
                break;
            case 2:
                writer.Append("Draws: ");
                writer.Append(stats.DrawCalls);
                writer.Append("  |  Submitted tris: ");
                writer.Append(stats.Triangles);
                break;
            case 3:
                writer.Append("Submeshes: ");
                writer.Append(stats.DrawCalls);
                writer.Append(" submitted / ");
                writer.Append(stats.Submeshes.Culled);
                writer.Append(" CPU culled");
                break;
            case 4:
                writer.Append("Material binds: ");
                writer.Append(stats.MaterialBinds);
                writer.Append("  |  Alloc: ");
                writer.Append(stats.AllocatedBytes / 1024.0, "F1");
                writer.Append(" KiB");
                break;
            default:
                if (sceneLoadMilliseconds is { } elapsed)
                {
                    writer.Append("Scene load: ");
                    writer.Append(elapsed, "F1");
                    writer.Append(" ms  |  ");
                }
                writer.Append("Texture payload: ");
                writer.Append(textureBytes / 1048576.0, "F1");
                writer.Append(" MiB");
                break;
        }
        return writer.Length;
    }

    RenderFrameStats Average()
    {
        long draws = 0, triangles = 0, binds = 0, allocated = 0, culled = 0;
        for (var i = 0; i < count; i++)
        {
            var stats = samples[i].Stats;
            draws += stats.DrawCalls;
            triangles += stats.Triangles;
            binds += stats.MaterialBinds;
            allocated += stats.AllocatedBytes;
            culled += stats.Submeshes.Culled;
        }
        var divisor = Math.Max(1, count);
        return new RenderFrameStats
        {
            DrawCalls = (int)(draws / divisor),
            Triangles = triangles / divisor,
            MaterialBinds = (int)(binds / divisor),
            AllocatedBytes = allocated / divisor,
            Submeshes = new RenderCullingStats((int)(draws / divisor), (int)(culled / divisor)),
        };
    }

    ref struct StatisticsText(Span<char> destination)
    {
        readonly Span<char> buffer = destination;
        int written;
        bool failed;

        /// <summary>The formatted length, or zero if the caller's buffer was too short.</summary>
        public readonly int Length => failed ? 0 : written;

        /// <summary>Appends a literal without allocating.</summary>
        public void Append(ReadOnlySpan<char> text)
        {
            if (text.TryCopyTo(buffer[written..])) written += text.Length;
            else failed = true;
        }

        /// <summary>Appends an integer without boxing.</summary>
        public void Append(long value)
        {
            if (value.TryFormat(buffer[written..], out var length, provider: CultureInfo.InvariantCulture))
                written += length;
            else failed = true;
        }

        /// <summary>Appends a fixed-precision floating point value without boxing.</summary>
        public void Append(double value, ReadOnlySpan<char> format)
        {
            if (value.TryFormat(buffer[written..], out var length, format, CultureInfo.InvariantCulture))
                written += length;
            else failed = true;
        }
    }
}
