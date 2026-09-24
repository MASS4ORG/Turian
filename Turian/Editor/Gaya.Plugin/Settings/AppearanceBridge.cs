namespace Gaya.Plugin.Turian;

/// <summary>
/// Keeps the appearance page and the theme service pointing at the same thing: the stored theme and
/// scales are applied at startup and after any edit, and a theme chosen from View / Themes is written
/// back into the page so it survives a restart.
/// </summary>
sealed class AppearanceBridge : IDisposable
{
    /// <summary>The id the appearance page's values are stored under.</summary>
    public const string PageId = "gaya.turian.appearance";

    readonly AppearanceSettings appearance;
    readonly IThemeService themes;
    readonly IEditorSettings settings;

    /// <summary>Binds the page to the theme service and applies what was stored.</summary>
    /// <param name="appearance">The page's settings object.</param>
    /// <param name="themes">The service the page drives.</param>
    /// <param name="settings">Told when the theme menu changed what the page holds.</param>
    public AppearanceBridge(AppearanceSettings appearance, IThemeService themes, IEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(settings);

        this.appearance = appearance;
        this.themes = themes;
        this.settings = settings;

        settings.Changed += Apply;
        themes.Changed += Remember;
        Apply();
    }

    /// <summary>Pushes the page's values onto the theme service. Both calls ignore a repeat.</summary>
    void Apply()
    {
        themes.Apply(appearance.Theme);
        themes.SetScale(appearance.TextSize, appearance.Zoom);
    }

    /// <summary>
    /// Records a theme committed from the menu. A preview leaves the committed name alone, so nothing
    /// is stored while the pointer travels down the list.
    /// </summary>
    void Remember()
    {
        if (string.Equals(themes.CommittedName, appearance.Theme, StringComparison.Ordinal)) return;

        appearance.Theme = themes.CommittedName;
        settings.NotifyChanged(PageId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        settings.Changed -= Apply;
        themes.Changed -= Remember;
    }
}
