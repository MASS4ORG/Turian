namespace Turian.Editor.Core;

/// <summary>
/// Finds an editor's executable on <c>PATH</c> and in the folders it usually installs to. An editor supplies its own
/// per-OS folders, so this stays free of any one platform's layout.
/// </summary>
public static class ExecutableLocator
{
    /// <summary>The first existing executable named <paramref name="names"/>, searching <c>PATH</c> and then the extra folders.</summary>
    /// <param name="names">File names without extension, in order of preference, such as <c>code</c>.</param>
    /// <param name="extraDirectories">The folders to search after <c>PATH</c>; missing ones are skipped.</param>
    /// <param name="environment">Reads an environment variable; the process environment when <c>null</c>.</param>
    /// <param name="fileExists">Tells whether a file exists; the file system when <c>null</c>.</param>
    /// <param name="windows">Whether to try the <c>PATHEXT</c> extensions; this OS's answer when <c>null</c>.</param>
    /// <returns>The full path, or <c>null</c> when none is found.</returns>
    public static string? Find(IEnumerable<string> names, IEnumerable<string>? extraDirectories = null,
        Func<string, string?>? environment = null, Func<string, bool>? fileExists = null, bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        environment ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;
        var extensions = (windows ?? OperatingSystem.IsWindows())
            ? Extensions(environment("PATHEXT"))
            : [string.Empty];

        var directories = (environment("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static directory => directory.Trim('"'))
            .Concat(extraDirectories ?? [])
            .Where(static directory => directory.Length > 0)
            .ToList();

        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (fileExists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    static string[] Extensions(string? pathExt)
    {
        var listed = (pathExt ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // An extensionless file is a shell script on Windows, which cannot be started directly, so it comes last.
        return [.. listed.Length > 0 ? listed : [".exe", ".cmd", ".bat"], string.Empty];
    }
}
