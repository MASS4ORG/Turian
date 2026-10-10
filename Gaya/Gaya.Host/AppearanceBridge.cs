namespace Gaya.Host;

/// <summary>
/// Keeps the appearance page and the theme service pointing at the same thing: the stored theme and
/// scales are applied at startup and after any edit, and a theme chosen from View / Themes is written
/// back into the page so it survives a restart.
/// </summary>
sealed class AppearanceBridge : IDisposable
{
    readonly AppearanceSettings appearance;
    readonly IThemeService themes;
    readonly IEditorSettings settings;
    bool applying;

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
        applying = true;
        try
        {
            themes.ApplyLook(appearance.Look);
            themes.ApplyColorTheme(appearance.Theme);
            themes.ApplyIconTheme(appearance.IconTheme);
            themes.SetScale(appearance.TextSize, appearance.Zoom);
        }
        finally
        {
            applying = false;
        }
        Remember();
    }

    /// <summary>
    /// Records a look, theme or icon theme committed from the menu. A preview leaves the committed ids alone, so
    /// nothing is stored while the pointer travels down the list.
    /// </summary>
    void Remember()
    {
        if (applying) return;

        var changed = Update(appearance.Theme, themes.CommittedColorTheme, value => appearance.Theme = value);
        changed |= Update(appearance.Look, themes.CommittedLook, value => appearance.Look = value);
        changed |= Update(appearance.IconTheme, themes.CommittedIconTheme, value => appearance.IconTheme = value);
        if (changed) settings.NotifyChanged(AppearanceSettings.PageId);
    }

    static bool Update(string stored, string committed, Action<string> store)
    {
        if (string.Equals(stored, committed, StringComparison.Ordinal)) return false;

        store(committed);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        settings.Changed -= Apply;
        themes.Changed -= Remember;
    }
}
