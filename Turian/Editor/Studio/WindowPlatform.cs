namespace Turian.Editor.Studio;

/// <summary>Selects a movable desktop backend when X11 or XWayland is available.</summary>
static class WindowPlatform
{
    internal static void Configure(bool linux, Func<string, string?> read, Action<string, string?> write)
    {
        if (!linux || string.IsNullOrEmpty(read("DISPLAY")) || read("SILKNET_USE_WAYLAND") is not null) return;
        write("SILKNET_USE_WAYLAND", "0");
    }
}
