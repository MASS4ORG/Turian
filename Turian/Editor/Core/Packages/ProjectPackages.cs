using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// The packages a Turian project installs, resolved through Gaya's package system with Turian as the host. A
/// resolution is reused until the project's manifest, user override or lock file changes.
/// </summary>
public static class ProjectPackages
{
    /// <summary>The host name packages state engine ranges and categories for.</summary>
    public const string HostName = "turian";

    /// <summary>The variable that moves the package store, such as onto a CI cache.</summary>
    public const string StoreVariable = "TURIAN_PACKAGES";

    static readonly Lock CacheLock = new();
    static readonly Dictionary<string, (string Stamp, PackageResolution Resolution)> Cache = [];

    /// <summary>This engine's version, which packages' <c>engines.turian</c> ranges are checked against.</summary>
    public static SemanticVersion EngineVersion { get; } = ReadEngineVersion();

    /// <summary>
    /// Resolves the packages of the project at <paramref name="projectRoot"/>, fetching git sources as needed, and
    /// writes its lock file unless a per-user override was applied.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="locked">Install exactly what the lock file records, as CI builds do.</param>
    /// <returns>The resolved packages, dependencies first.</returns>
    /// <exception cref="PackageException">A package cannot be fetched or does not fit.</exception>
    public static PackageResolution Resolve(string projectRoot, bool locked = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);
        var stamp = Stamp(projectRoot, locked);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(projectRoot, out var cached) && cached.Stamp == stamp) return cached.Resolution;
        }

        var resolver = new PackageResolver(new PackageStore(PackageStore.DefaultRoot(HostName, StoreVariable)),
            new PackageResolverOptions
            {
                Hosts = new Dictionary<string, SemanticVersion> { [HostName] = EngineVersion },
                ReservedCategoryPrefixes = ["gaya", HostName],
                Locked = locked,
            });
        var resolution = resolver.ResolveAsync(projectRoot).GetAwaiter().GetResult();
        PackageAssetIds.EnsureUnique(Path.Combine(projectRoot, "Assets"), resolution.Packages);

        if (resolution.UsesUserOverride)
            Log.Logger.LogInformation("Packages resolved with {File}; the lock file is left unchanged",
                ProjectManifest.UserFileName);
        else if (!locked)
            resolution.Lock.Save(projectRoot);

        lock (CacheLock)
        {
            Cache[projectRoot] = (Stamp(projectRoot, locked), resolution);
        }

        return resolution;
    }

    /// <summary>The package folders of a project, dependencies first; none when it declares no packages.</summary>
    /// <param name="projectRoot">The project folder, or empty for none.</param>
    /// <returns>The resolved packages.</returns>
    public static IReadOnlyList<ResolvedPackage> ResolveOrEmpty(string? projectRoot) =>
        string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(Path.Combine(projectRoot, ProjectManifest.DirectoryName))
            ? []
            : Resolve(projectRoot).Packages;

    static string Stamp(string projectRoot, bool locked)
    {
        var directory = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var files = new[] { ProjectManifest.FileName, ProjectManifest.UserFileName, LockFile.FileName }
            .Select(name => Path.Combine(directory, name))
            .Select(static path => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        IEnumerable<string> embedded = Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            : [];

        return $"{locked}|{string.Join('|', files)}|{string.Join('|', embedded)}";
    }

    static SemanticVersion ReadEngineVersion()
    {
        var assembly = typeof(Component).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var version)) return version;

        var fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemanticVersion(fallback.Major, fallback.Minor, Math.Max(fallback.Build, 0));
    }
}
