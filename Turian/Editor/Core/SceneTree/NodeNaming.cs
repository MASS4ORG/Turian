namespace Turian.Editor.Core;

/// <summary>
/// Utilities for generating unique node names within a sibling scope.
/// </summary>
public static class NodeNaming
{
    /// <summary>
    /// Returns <paramref name="baseName"/> if not already taken, otherwise appends
    /// an incrementing number until a free name is found.
    /// </summary>
    public static string GetNextAvailable(string baseName, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(existingNames);
        var names = existingNames.ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(baseName)) return baseName;

        var match = Regex.Match(baseName, @"^(.*?)(?:\s+(\d+))?$");
        var stem = match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : baseName.Trim();

        var n = 1;
        string candidate;
        do { candidate = $"{stem} {n++}"; }
        while (names.Contains(candidate));

        return candidate;
    }
}
