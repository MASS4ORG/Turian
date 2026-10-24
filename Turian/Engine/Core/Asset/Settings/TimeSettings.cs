namespace Turian.Engine.Core;

/// <summary>Configures the authoritative simulation interval and the work budget of its frame driver.</summary>
[CreateAssetMenu(fileName: "TimeSettings", path: "Settings/Time Settings")]
[TypeId("30caacaf-7e76-47ea-9f56-f1ce39ae52dc")]
public class TimeSettings : ProjectSettingsAsset
{
    /// <summary>Seconds simulated by one fixed tick.</summary>
    public double FixedDeltaTime { get; set; } = 1d / 60d;

    /// <summary>Maximum ticks executed per driver frame; excess accumulated time is retained.</summary>
    public int MaxTicksPerFrame { get; set; } = 8;

    /// <summary>
    /// Drops the time a frame cannot simulate within <see cref="MaxTicksPerFrame"/>, keeping only the partial tick, so
    /// an overloaded game slows down instead of spending later frames catching up. Authoritative servers keep it off.
    /// </summary>
    public bool DropBacklog { get; set; }
}
