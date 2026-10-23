using System.Text.RegularExpressions;

namespace MASS4.Bricks;

/// <summary>
/// The globs of a manifest's <c>exclude</c> list, matched against <c>/</c>-separated paths relative to the brick folder.
/// <c>*</c> and <c>?</c> stay inside one name and <c>**</c> spans folders. As in <c>.gitignore</c>, a glob without
/// <c>/</c> matches a name at any depth, and a glob matching a folder matches everything in it.
/// </summary>
public static class BrickGlob
{
    /// <summary>Whether <paramref name="glob"/> can be matched: relative, <c>/</c>-separated, never leaving the folder.</summary>
    /// <param name="glob">The glob.</param>
    /// <returns>True for a usable glob.</returns>
    public static bool IsValid([NotNullWhen(true)] string? glob) =>
        !string.IsNullOrWhiteSpace(glob) && !glob.Contains('\\') && !Path.IsPathRooted(glob)
        && glob.Trim('/').Split('/').All(static segment => segment is not ("" or "." or ".."));

    /// <summary>Whether <paramref name="relativePath"/>, or a folder holding it, matches <paramref name="glob"/>.</summary>
    /// <param name="glob">A glob that <see cref="IsValid"/> accepts.</param>
    /// <param name="relativePath">A file path relative to the brick folder, <c>/</c>-separated.</param>
    /// <returns>True when the file is excluded.</returns>
    public static bool Matches(string glob, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var regex = Compile(glob);
        var segments = relativePath.Split('/');
        for (var count = 1; count <= segments.Length; count++)
        {
            if (regex.IsMatch(string.Join('/', segments[..count])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="globs"/> matches <paramref name="relativePath"/>.</summary>
    /// <param name="globs">The globs.</param>
    /// <param name="relativePath">A file path relative to the brick folder, <c>/</c>-separated.</param>
    /// <returns>True when the file is excluded.</returns>
    public static bool MatchesAny(IEnumerable<string> globs, string relativePath) =>
        globs.Any(glob => Matches(glob, relativePath));

    static Regex Compile(string glob)
    {
        var trimmed = glob.Trim('/');
        var pattern = glob.TrimEnd('/').Contains('/') ? trimmed : $"**/{trimmed}";
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    regex.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    i += followedBySlash ? 2 : 1;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
