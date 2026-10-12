namespace Turian.Editor.Core;

/// <summary>
/// Finds the <c>dotnet</c> executable every build, restore and play process starts, so that an SDK the user
/// configured, or one a platform requires, wins over whichever one happens to be first on <c>PATH</c>.
/// </summary>
public static class DotnetLocator
{
    /// <summary>The environment variable naming a .NET installation folder.</summary>
    public const string RootVariable = "DOTNET_ROOT";

    /// <summary>The <c>dotnet</c> executable to start.</summary>
    /// <param name="preferredRoot">A configured installation folder tried first, or <c>null</c>.</param>
    /// <param name="requiredSdkMajor">The SDK major version the build needs, or <c>null</c> for any.</param>
    /// <param name="environment">Reads an environment variable; the process environment when <c>null</c>.</param>
    /// <returns>
    /// The full path of the first installation that qualifies, trying the preferred folder, then
    /// <c>DOTNET_ROOT</c>, then <c>PATH</c>; the bare name <c>dotnet</c> when nothing is found and no version is required.
    /// </returns>
    /// <exception cref="InvalidOperationException">No installation provides the required SDK; the message says which were checked.</exception>
    public static string Find(string? preferredRoot = null, int? requiredSdkMajor = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredRoot)) roots.Add(preferredRoot);
        if (environment(RootVariable) is { Length: > 0 } dotnetRoot) roots.Add(dotnetRoot);
        roots.AddRange((environment("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var checkedRoots = new List<string>();
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, executable);
            if (!File.Exists(candidate)) continue;

            checkedRoots.Add(root);
            if (requiredSdkMajor is not { } major || HasSdk(InstallationRoot(candidate), major)) return candidate;
        }

        if (requiredSdkMajor is not { } required) return "dotnet";

        throw new InvalidOperationException(checkedRoots.Count == 0
            ? $"No dotnet executable was found, and the build needs the .NET {required} SDK."
            : $"The build needs the .NET {required} SDK, which is not installed in: {string.Join(", ", checkedRoots)}.");
    }

    // PATH usually holds a symlink; the SDKs live beside the real executable.
    static string InstallationRoot(string executable)
    {
        var real = File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName ?? executable;
        return Path.GetDirectoryName(real)!;
    }

    static bool HasSdk(string root, int major)
    {
        var sdks = Path.Combine(root, "sdk");
        return Directory.Exists(sdks)
               && Directory.EnumerateDirectories(sdks)
                   .Any(directory => Path.GetFileName(directory).StartsWith($"{major}.", StringComparison.Ordinal));
    }
}
