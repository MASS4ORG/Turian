namespace Gaya.Host;

/// <summary>Stores the desktop editor's frame rate limit.</summary>
[Gaya.EditorSetting("General/Performance", Id = PageId)]
public sealed class FrameRateSettings
{
    /// <summary>The stable id of the performance settings page.</summary>
    public const string PageId = "gaya.performance";

    /// <summary>Gets or sets the maximum frames per second, with zero disabling the limit.</summary>
    [Gaya.EditorSetting("Cap FPS", Description = "Maximum editor frames per second. Set to 0 for uncapped rendering.")]
    [Range(0, 1000)]
    public int CapFps
    {
        get => field;
        set => field = Math.Max(0, value);
    } = 60;

    /// <summary>Registers the shared performance page and restores persisted preferences.</summary>
    public static FrameRateSettings Register(IEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Pages.FirstOrDefault(page => page.Id == PageId)?.Target is FrameRateSettings existing)
            return existing;
        var preferences = new FrameRateSettings();
        settings.Register(preferences);
        return preferences;
    }
}
