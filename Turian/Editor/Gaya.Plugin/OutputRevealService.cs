namespace Gaya.Plugin.Turian;

/// <summary>
/// Asks the Output panel to select a log entry. The status bar raises it when its latest-message readout is
/// clicked; the panel takes the request on its next frame, makes the entry visible and selects it.
/// </summary>
public sealed class OutputRevealService
{
    LogLine? pending;

    /// <summary>Asks the Output panel to show and select <paramref name="line"/>.</summary>
    /// <param name="line">The entry to reveal.</param>
    public void Request(LogLine line) => pending = line;

    /// <summary>Hands the panel the requested entry once, or null when nothing was asked for.</summary>
    /// <returns>The entry to reveal.</returns>
    internal LogLine? Take()
    {
        var line = pending;
        pending = null;
        return line;
    }
}
