namespace Gaya.Host;

/// <summary>The workbench's theme, interface scale and desktop window decorations.</summary>
[EditorSetting("Appearance", Description = "Theme, interface size and window decorations.")]
public sealed class AppearanceSettings
{
    int textSize = 12;

    /// <summary>The id of the appearance page in the settings API.</summary>
    public const string PageId = "gaya.appearance";

    /// <summary>The name of the committed theme.</summary>
    [EditorSetting("Theme", Description = "Colors used throughout the workbench.")]
    public string Theme { get; set; } = "Dark";

    /// <summary>The workbench's base text size in points.</summary>
    [EditorSetting("Text Size", Description = "Base interface text size in points.")]
    [Range(9, 24)]
    public int TextSize
    {
        get => textSize;
        set => textSize = value is < 9 or > 24 ? 12 : value;
    }

    /// <summary>Multiplies the measured sizes of rows, tabs, buttons and toolbars.</summary>
    [EditorSetting("Zoom", Description = "Scales button, row and tab heights.")]
    [Range(0.6f, 2f)]
    public float Zoom { get; set; } = 1f;

    /// <summary>Whether the operating system supplies the title bar and window buttons.</summary>
    [EditorSetting("Native Title Bar",
        Description = "Use operating system decorations. Disable for application window controls and dragging.")]
    public bool NativeTitlebar { get; set; }

    /// <summary>Registers the shared page, restoring preferences from an optional application-owned page.</summary>
    /// <param name="settings">The registry and storage used by the workbench.</param>
    /// <param name="previousPageId">The application's former appearance page id, when migrating preferences.</param>
    /// <returns>The shared appearance settings object.</returns>
    public static AppearanceSettings Register(EditorSettings settings, string? previousPageId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Pages.FirstOrDefault(page => page.Id == PageId)?.Target is AppearanceSettings existing)
            return existing;

        var appearance = new AppearanceSettings();
        var page = new SettingsPageDescriptor(PageId, "Appearance", appearance)
        {
            Description = "Theme, interface size and window decorations.",
        };
        if (previousPageId is not null) settings.RestoreUserPage(page with { Id = previousPageId });
        settings.RestoreUserPage(page with { Id = "gaya.window" });
        settings.Register(page);
        return appearance;
    }
}
