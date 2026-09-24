namespace Turian.Engine.Core;

/// <summary>
/// Static entry point for game code and UI documents, resolving the active <see cref="LocaleService"/>
/// from <see cref="RuntimeServices"/> the same way <c>InputActions</c> resolves the input service.
/// </summary>
/// <remarks>
/// Every member is neutral when no service is registered, so a script or document can call
/// <see cref="T"/> before the project's tables are loaded without throwing. The service itself is
/// reachable through <see cref="Service"/> for handlers that need its events.
/// </remarks>
public static class Localization
{
    /// <summary>The active service, or <c>null</c> when none is registered.</summary>
    public static LocaleService? Service => RuntimeServices.TryGet<LocaleService>();

    /// <summary>Whether a localization service is in force.</summary>
    public static bool IsAvailable => Service is not null;

    /// <summary>The active locale, or <c>en</c> when no service is registered.</summary>
    public static string ActiveLocale => Service?.ActiveLocale ?? "en";

    /// <summary>Switches the active locale; no-op when no service is registered.</summary>
    /// <param name="locale">The BCP-47 locale to switch to.</param>
    /// <returns><c>true</c> when the locale actually changed.</returns>
    public static bool SetLocale(string locale) => Service?.SetLocale(locale) ?? false;

    /// <summary>Resolves a key, falling back to the key itself when there is no service or no entry.</summary>
    /// <param name="key">The localization key.</param>
    /// <param name="fallback">Text to return when no table holds the key; defaults to the key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The localized, formatted string.</returns>
    public static string T(string key, string? fallback = null, decimal? count = null) =>
        Service?.Translate(key, fallback ?? key, count) ?? fallback ?? key;

    /// <summary>Resolves authored source text, returning it unchanged when there is no translation.</summary>
    /// <param name="source">The authored text, also used as the lookup key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The localized string, or <paramref name="source"/>.</returns>
    public static string Source(string source, decimal? count = null) =>
        Service?.TranslateSource(source, count) ?? source;

    /// <summary>
    /// The text a <c>.ui</c> element should draw: <paramref name="key"/> when the document names one
    /// explicitly, otherwise <paramref name="source"/> used as its own key. Both paths return the
    /// source unchanged when there is no service or no matching entry.
    /// </summary>
    /// <param name="key">An explicit localization key, or <c>null</c>/empty when the document has none.</param>
    /// <param name="source">The authored text.</param>
    /// <returns>The localized text.</returns>
    public static string Resolve(string? key, string? source)
    {
        var service = Service;

        if (key is { Length: > 0 })
            return service?.Translate(key, source ?? key) ?? source ?? key;

        if (string.IsNullOrEmpty(source)) return string.Empty;
        return service?.TranslateSource(source) ?? source;
    }
}
