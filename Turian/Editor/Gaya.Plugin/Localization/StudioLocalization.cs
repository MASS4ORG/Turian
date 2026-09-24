namespace Gaya.Plugin.Turian;

/// <summary>
/// The studio shell's own language service, kept apart from the game's localization: the editor's
/// labels come from its embedded tables and the project's <c>LocaleService</c> translates the UI
/// documents being edited. Its keys are the authored English shell strings themselves, so
/// <see cref="T"/> needs no key catalog.
/// </summary>
sealed class StudioLocalization : IShellLocalization
{
    readonly LocaleService locale;
    readonly string[] embeddedLocales;

    /// <summary>Creates the service and loads every embedded <c>.strings</c> table.</summary>
    public StudioLocalization()
    {
        var assembly = typeof(StudioLocalization).Assembly;
        embeddedLocales = [.. assembly.GetManifestResourceNames().Where(name => name.EndsWith(".strings", StringComparison.Ordinal))];

        locale = new LocaleService();
        locale.LocaleChanged += name => LocaleChanged?.Invoke(name);

        foreach (var resource in embeddedLocales)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"Unable to load localization resource '{resource}'.");
            using var reader = new StreamReader(stream);
            locale.AddTable(StringTableJson.Load(reader.ReadToEnd()));
        }
    }

    /// <summary>Raised after the active shell language changes.</summary>
    public event Action<string>? LocaleChanged;

    /// <summary>The BCP-47 locale the shell is currently drawn in.</summary>
    public string ActiveLocale => locale.ActiveLocale;

    /// <summary>The locales this shell ships tables for, plus the English source.</summary>
    public IReadOnlyList<string> AvailableLocales =>
        ["en", .. embeddedLocales.Select(LocaleOfResource).Where(name => !string.Equals(name, "en", StringComparison.OrdinalIgnoreCase))];

    /// <summary>Translates a shell string by its English source; the source passes through untranslated.</summary>
    /// <param name="source">The authored English string.</param>
    /// <returns>The shell language's rendering of it.</returns>
    public string T(string source) => locale.Translate(source, source);

    /// <summary>Switches the shell language.</summary>
    /// <param name="localeName">A BCP-47 locale the shell ships, or <c>en</c>.</param>
    /// <returns><c>true</c> when the language actually changed.</returns>
    public bool SetLocale(string localeName) => locale.SetLocale(localeName);

    static string LocaleOfResource(string resourceName)
    {
        var segments = resourceName.Split('.');
        return segments[^2].Replace('_', '-');
    }
}
