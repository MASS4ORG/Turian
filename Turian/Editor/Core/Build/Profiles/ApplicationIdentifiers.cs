namespace Turian.Editor.Core;

/// <summary>
/// Application identifiers as platforms that need a Java package name read them: two or more dot-separated
/// segments, each an ASCII letter followed by ASCII letters, digits or underscores, and none a Java keyword.
/// </summary>
public static class ApplicationIdentifiers
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false", "null", "_",
    };

    /// <summary>Turns free text into one valid identifier segment, such as a project title.</summary>
    /// <param name="value">The text.</param>
    /// <returns>A lowercase segment of ASCII letters, digits and underscores; <c>game</c> for none.</returns>
    public static string SanitizeSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var chars = value.Select(static ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray();
        var segment = new string(chars).Trim('_');
        if (segment.Length == 0) return "game";
        if (!char.IsAsciiLetter(segment[0])) segment = "g" + segment;
        return Keywords.Contains(segment) ? segment + "_" : segment;
    }

    /// <summary>Checks an identifier a platform needs to be a Java package name.</summary>
    /// <param name="identifier">The player settings' application identifier.</param>
    /// <returns>What is wrong with it, or <c>null</c> when it is valid.</returns>
    public static string? Validate(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "The application identifier is empty.";

        var segments = identifier.Split('.');
        if (segments.Length < 2)
            return $"The application identifier '{identifier}' needs at least two dot-separated segments.";

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetter(segment[0]) || !segment.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return $"The application identifier '{identifier}' has the invalid segment '{segment}': "
                       + "segments start with a letter and hold only letters, digits and underscores.";
            if (Keywords.Contains(segment))
                return $"The application identifier '{identifier}' uses '{segment}', a reserved Java word.";
        }

        return null;
    }
}
