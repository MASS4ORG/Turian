using System.Globalization;

namespace Gaya.Host;

/// <summary>Formats the status bar's right-hand readout: frame rate, frame time and the studio's memory.</summary>
public static class StatusReadout
{
    /// <summary>The readout line, such as <c>60 fps · 16.7 ms · 1.4 GB · Gaya</c>.</summary>
    /// <param name="fps">The smoothed frames per second; zero before the first measurement.</param>
    /// <param name="memoryBytes">The studio process's memory in bytes.</param>
    /// <returns>The text to draw.</returns>
    public static string Format(float fps, long memoryBytes) => string.Create(CultureInfo.InvariantCulture,
        $"{fps:0} fps  ·  {FrameMilliseconds(fps):0.0} ms  ·  {Bytes(memoryBytes)}  ·  Gaya");

    /// <summary>The time one frame takes at <paramref name="fps"/>, in milliseconds; zero when nothing is measured yet.</summary>
    /// <param name="fps">Frames per second.</param>
    /// <returns>Milliseconds per frame.</returns>
    public static float FrameMilliseconds(float fps) => fps > 0f ? 1000f / fps : 0f;

    /// <summary>A byte count in the largest unit that keeps it readable: MB below a gigabyte, then GB with one decimal.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The text, such as <c>512 MB</c> or <c>1.4 GB</c>.</returns>
    public static string Bytes(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB");
}
