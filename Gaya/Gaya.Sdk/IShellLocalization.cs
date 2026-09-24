namespace Gaya.Sdk;

/// <summary>
/// Translates the workbench's own chrome — menu titles, panel titles — into whatever language a
/// plugin's shell language service is set to. The host draws this chrome without knowing which
/// plugin owns localization, so it depends on this interface rather than a concrete service; a host
/// with no such plugin resolves nothing and chrome stays in its authored language.
/// </summary>
public interface IShellLocalization
{
    /// <summary>Translates a shell string by its authored English source, unchanged when untranslated.</summary>
    /// <param name="source">The authored English string.</param>
    string T(string source);

    /// <summary>Raised after the active shell language changes, so drawn chrome is not the concern.</summary>
    event Action<string>? LocaleChanged;
}
