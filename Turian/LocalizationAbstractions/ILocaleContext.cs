namespace MASS4.Localization;

/// <summary>
/// The active locale and the text it resolves: the only localization surface interface bricks and hosts
/// depend on, so the implementation behind it can change without touching them.
/// </summary>
public interface ILocaleContext
{
    /// <summary>The locale currently in force.</summary>
    string ActiveLocale { get; }

    /// <summary>Raised after <see cref="ActiveLocale"/> changes, with the new locale.</summary>
    event Action<string>? LocaleChanged;

    /// <summary>Switches the active locale.</summary>
    /// <param name="locale">The BCP-47 locale to switch to.</param>
    /// <returns><c>true</c> when the active locale actually changed.</returns>
    bool SetLocale(string locale);

    /// <summary>Resolves a key to display text.</summary>
    /// <param name="key">The localization key.</param>
    /// <param name="fallback">Text to return when the key is unknown, or <c>null</c> for an implementation default.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The display text.</returns>
    string Translate(string key, string? fallback = null, decimal? count = null);

    /// <summary>Resolves authored text, which is also its own fallback.</summary>
    /// <param name="source">The authored text, also used as the lookup key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The display text, or <paramref name="source"/> when nothing translates it.</returns>
    string TranslateSource(string source, decimal? count = null);
}
