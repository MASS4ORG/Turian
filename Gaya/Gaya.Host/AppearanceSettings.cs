namespace Gaya.Host;

/// <summary>The workbench's look, color theme, icon theme, interface scale and desktop window decorations.</summary>
[EditorSetting("Appearance", Id = PageId, Description = "Look, theme, icons, interface size and window decorations.")]
public sealed class AppearanceSettings
{
    int textSize = 12;

    /// <summary>The id of the appearance page in the settings API.</summary>
    public const string PageId = "gaya.appearance";

    /// <summary>The id of the committed color theme; names stored by older versions are still recognized.</summary>
    [EditorSetting("Theme", Description = "Colors used throughout the workbench.")]
    public string Theme { get; set; } = ThemeCatalog.DefaultColorTheme;

    /// <summary>The id of the committed look, which shapes every control independently of the colors.</summary>
    [EditorSetting("Look", Description = "Shape, borders and motion of controls. Choosing one applies its suggested theme.")]
    public string Look { get; set; } = ThemeCatalog.DefaultLook;

    /// <summary>The id of the committed icon theme.</summary>
    [EditorSetting("Icons", Description = "The icon set used throughout the workbench.")]
    public string IconTheme { get; set; } = ThemeCatalog.DefaultIconTheme;

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
        var page = new SettingsPageDescriptor(appearance);
        if (previousPageId is not null) settings.RestoreUserPage(page, previousPageId);
        settings.RestoreUserPage(page, "gaya.window");
        settings.RegisterPage(page);
        return appearance;
    }
}
