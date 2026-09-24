namespace Gaya.Plugin.Turian;

/// <summary>
/// The Output console's preferences: what it shows (timestamp, monospace, how many lines an entry
/// may occupy) and when it empties itself (entering play, a completed build, a fresh user assembly
/// from a recompile). Stored with the user as a hidden settings page, and bound by the panel's own
/// settings row — the same object the Settings panel would edit if it were not hidden.
/// </summary>
[EditorSetting("Studio/Output", Description = "How the Output console displays its entries and when it clears itself.")]
public sealed class OutputPanelSettings
{
    /// <summary>The settings page's id, keying the stored values and the panel's change reports.</summary>
    public const string PageId = "gaya.turian.outputPanel";

    /// <summary>Whether each entry is prefixed with the time its message arrived.</summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>Whether entries are drawn in a monospaced font.</summary>
    public bool Monospace { get; set; }

    /// <summary>Whether the console clears when a play session starts.</summary>
    public bool ClearOnPlay { get; set; } = true;

    /// <summary>Whether the console clears when a standalone build (Build &amp; Run or Export) completes.</summary>
    public bool ClearOnBuild { get; set; } = true;

    /// <summary>Whether the console clears when user code recompiles and a new assembly is loaded.</summary>
    public bool ClearOnRecompile { get; set; } = true;

    int entryLines = 1;

    /// <summary>How many lines of a message an entry in the main list shows at most.</summary>
    public int EntryLines
    {
        get => entryLines;
        set => entryLines = value is >= 1 and <= 6 ? value : 1;
    }
}
