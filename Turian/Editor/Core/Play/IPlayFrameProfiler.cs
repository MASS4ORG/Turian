namespace Turian.Editor.Core;

/// <summary>Collects timings around synchronous Play Mode work when a profiler is installed.</summary>
public interface IPlayFrameProfiler
{
    /// <summary>Opens one capture on the calling thread, or returns an empty scope when idle.</summary>
    PlayFrameScope BeginFrame(PlayFrameSource source);
}

/// <summary>Identifies a Play Mode capture boundary.</summary>
public enum PlayFrameSource
{
    /// <summary>A simulation tick or explicit step.</summary>
    Simulation,

    /// <summary>The Game viewport drawing a running scene.</summary>
    Viewport,
}

/// <summary>Closes a synchronous capture using a collector callback and its frame token.</summary>
/// <param name="complete">Reusable callback that finishes the frame.</param>
/// <param name="token">Collector token for the frame.</param>
public readonly struct PlayFrameScope(Action<long> complete, long token) : IDisposable
{
    /// <summary>Completes the frame on the thread that opened it.</summary>
    public void Dispose() => complete?.Invoke(token);
}
