namespace Turian.Editor.Core;

/// <summary>
/// Extracts the source-keyed localization calls the engine's UI documents and scripts rely on, so the
/// strings they use can seed an English table. Matching <c>T("text")</c> and <c>t("text")</c> covers
/// <see cref="Localization.T"/> and the Studio shorthand <c>Gaya.Plugin.Turian.StudioLocalization.T</c>.
/// </summary>
/// <remarks>
/// This is a heuristic aid for authoring a project's first English table, not a semantic analyser:
/// it deliberately ignores calls with expressions or interpolations and reports matches in order of
/// the files scanned.
/// </remarks>
public static partial class LocalizationSourceExtractor
{
    [GeneratedRegex(@"\b(?:T|t)\s*\(\s*""(?<text>(?:\\.|[^""\\])*)""\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex TranslationCallRegex();

    /// <summary>Gathers the distinct string literals passed to <c>T(...)</c> across the given files.</summary>
    /// <param name="sourceFiles">Absolute paths of the C# source files to scan.</param>
    /// <returns>The distinct literals, sorted ordinally.</returns>
    public static IReadOnlyList<string> Extract(IEnumerable<string> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        return sourceFiles
            .SelectMany(File.ReadLines)
            .SelectMany(line => TranslationCallRegex().Matches(line).Select(match => match.Groups["text"].Value))
            .Select(Regex.Unescape)
            .Where(static text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static text => text, StringComparer.Ordinal)
            .ToArray();
    }
}
