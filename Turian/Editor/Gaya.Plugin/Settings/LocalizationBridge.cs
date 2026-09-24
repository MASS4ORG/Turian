namespace Gaya.Plugin.Turian;

/// <summary>
/// Keeps the Language page and <see cref="StudioLocalization"/> pointing at the same language: the
/// stored choice is applied at startup and after any edit, and a language chosen from
/// Edit / Localization is written back into the page so it survives a restart.
/// </summary>
sealed class LocalizationBridge : IDisposable
{
    /// <summary>The id the language page's values are stored under.</summary>
    public const string PageId = "gaya.turian.language";

    readonly StudioLanguageSettings language;
    readonly StudioLocalization localization;
    readonly IEditorSettings settings;

    /// <summary>Binds the page to the shell's localization service and applies what was stored.</summary>
    /// <param name="language">The page's settings object.</param>
    /// <param name="localization">The service the page drives.</param>
    /// <param name="settings">Told when the locale menu changed what the page holds.</param>
    public LocalizationBridge(StudioLanguageSettings language, StudioLocalization localization,
        IEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(settings);

        this.language = language;
        this.localization = localization;
        this.settings = settings;

        settings.Changed += Apply;
        localization.LocaleChanged += Remember;
        Apply();
    }

    /// <summary>Pushes the page's choice onto the localization service; a repeat is a no-op.</summary>
    void Apply() => localization.SetLocale(LocaleOf(language.Language));

    /// <summary>
    /// Records a language committed from the menu, so it is stored with the page rather than lost
    /// when the editor closes.
    /// </summary>
    void Remember(string locale)
    {
        if (string.Equals(locale, LocaleOf(language.Language), StringComparison.OrdinalIgnoreCase)) return;

        language.Language = LanguageOf(locale);
        settings.NotifyChanged(PageId);
    }

    static string LocaleOf(EditorLanguage language) => language switch
    {
        EditorLanguage.PortugueseBrazil => "pt-BR",
        _ => "en",
    };

    static EditorLanguage LanguageOf(string locale) =>
        locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? EditorLanguage.PortugueseBrazil
            : EditorLanguage.English;

    /// <inheritdoc />
    public void Dispose()
    {
        settings.Changed -= Apply;
        localization.LocaleChanged -= Remember;
    }
}
