namespace Turian.Engine.Core;

/// <summary>
/// The localization service shared by the Studio and running games: holds one <see cref="StringTable"/>
/// per locale, resolves a key through a locale → language → default fallback chain, and raises
/// <see cref="LocaleChanged"/> when the active locale changes.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a plain singleton rather than through <see cref="InternalServiceAttribute"/>, because
/// a table-less instance is useless: each host loads the project's tables into it (or a fresh play
/// scope) before anything reads a key. <see cref="Localization"/> is the static facade game code and
/// UI documents use.
/// </para>
/// <para>
/// Locale switching is live: <see cref="Generation"/> increments and <see cref="LocaleChanged"/> fires,
/// and immediate-mode UI re-resolves its text every frame, so nothing needs reloading.
/// </para>
/// </remarks>
public sealed class LocaleService
{
    readonly Dictionary<string, StringTable> tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a service with a default locale and no tables.</summary>
    /// <param name="defaultLocale">The locale reads fall back to; <c>en</c> when not given.</param>
    public LocaleService(string defaultLocale = "en")
    {
        DefaultLocale = string.IsNullOrWhiteSpace(defaultLocale) ? "en" : defaultLocale;
        ActiveLocale = DefaultLocale;
    }

    /// <summary>The locale unresolved keys fall back to.</summary>
    public string DefaultLocale { get; }

    /// <summary>The locale currently in force.</summary>
    public string ActiveLocale { get; private set; }

    /// <summary>Increments on every locale change, so a retained text mesh knows to rebuild.</summary>
    public uint Generation { get; private set; }

    /// <summary>Raised after <see cref="ActiveLocale"/> changes, with the new locale.</summary>
    public event Action<string>? LocaleChanged;

    /// <summary>Every locale a table has been added for.</summary>
    public IReadOnlyCollection<string> AvailableLocales => tables.Keys;

    /// <summary>Adds or replaces the table for its locale.</summary>
    /// <param name="table">The table to add.</param>
    public void AddTable(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        tables[table.Locale] = table;
    }

    /// <summary>Forgets every table, so a project switch starts clean.</summary>
    public void ClearTables() => tables.Clear();

    /// <summary>
    /// Switches the active locale. A locale with no table is allowed and simply falls through the
    /// fallback chain — the caller may add the table later without re-selecting.
    /// </summary>
    /// <param name="locale">The BCP-47 locale to switch to.</param>
    /// <returns><c>true</c> when the active locale actually changed.</returns>
    public bool SetLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        if (string.Equals(ActiveLocale, locale, StringComparison.OrdinalIgnoreCase)) return false;

        ActiveLocale = locale;
        Generation++;
        LocaleChanged?.Invoke(locale);
        return true;
    }

    /// <summary>
    /// Resolves a key through the fallback chain and formats any plural block.
    /// </summary>
    /// <param name="key">The localization key.</param>
    /// <param name="fallback">Text to return when no table holds the key; defaults to a bracketed key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The localized, formatted string.</returns>
    public string Translate(string key, string? fallback = null, decimal? count = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        foreach (var locale in FallbackChain(ActiveLocale))
        {
            if (tables.TryGetValue(locale, out var table) && table.Get(key, count) is { } value)
                return MessageFormatter.Format(value, count, locale);
        }

        return fallback ?? $"⟦{key}⟧";
    }

    /// <summary>
    /// Resolves authored source text through the fallback chain, falling back to the text itself. This
    /// is what makes every displayed string localizable without changing how it is authored: a table
    /// with the source as its key translates it, and a project with no table renders unchanged.
    /// </summary>
    /// <param name="source">The authored text, also used as the lookup key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The localized string, or <paramref name="source"/>.</returns>
    public string TranslateSource(string source, decimal? count = null) => Translate(source, source, count);

    /// <summary>Whether any table has been added.</summary>
    public bool HasTables => tables.Count > 0;

    IEnumerable<string> FallbackChain(string locale)
    {
        yield return locale;

        var separator = locale.IndexOfAny(['-', '_']);
        if (separator > 0) yield return locale[..separator];

        if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            yield return DefaultLocale;
            var defaultSeparator = DefaultLocale.IndexOfAny(['-', '_']);
            if (defaultSeparator > 0) yield return DefaultLocale[..defaultSeparator];
        }
    }
}
