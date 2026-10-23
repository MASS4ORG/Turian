using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>
/// The packages a Turian project installs, resolved through Gaya's package system with Turian as the host. A
/// resolution is reused until the project's manifest, user override or lock file changes.
/// </summary>
public static class ProjectPackages
{
    /// <summary>The host name packages state engine ranges and categories for.</summary>
    public const string HostName = "turian";

    /// <summary>
    /// The engine's built-in packages: the <c>packages</c> folder beside a published engine's assemblies, else the
    /// checkout's <c>Turian/Packages</c> it was built from.
    /// </summary>
    public static string BuiltinDirectory { get; } = FindBuiltinDirectory();

    /// <summary>The built-in packages a new project installs.</summary>
    public static IReadOnlyList<string> DefaultBuiltins { get; } = [RenderBrick];

    /// <summary>The built-in render brick: the Vulkan renderer and window client builds play in.</summary>
    public const string RenderBrick = "org.mass4.turian.vulkan";

    /// <summary>
    /// The bricks every project uses without declaring them; a project opts out by mapping the id to <c>null</c> in
    /// its manifest, which removing or disabling the brick does.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultDependencies { get; } =
        new Dictionary<string, string> { [RenderBrick] = $"builtin:{RenderBrick}" };

    static readonly Lock CacheLock = new();
    static readonly Dictionary<string, (string Stamp, PackageResolution Resolution)> Cache = [];

    // A failed resolution, kept like a good one: every caller gets it again without another network round, and it is logged once.
    static readonly Dictionary<string, (string Stamp, PackageException Failure)> Failures = [];

    // The kept failures already logged by ResolveOrEmpty.
    static readonly ConditionalWeakTable<PackageException, object> Reported = new();

    /// <summary>This engine's version, which packages' <c>engines.turian</c> ranges are checked against.</summary>
    public static SemanticVersion EngineVersion { get; } = ReadVersion(typeof(Component).Assembly);

    /// <summary>The Gaya platform's version, which packages' <c>engines.gaya</c> ranges are checked against.</summary>
    public static SemanticVersion GayaVersion { get; } = ReadVersion(typeof(PackageResolver).Assembly);

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
            if (Cache.TryGetValue(projectRoot, out var cached) && cached.Stamp == stamp)
            {
                return cached.Resolution;
            }

            if (Failures.TryGetValue(projectRoot, out var failed) && failed.Stamp == stamp)
            {
                throw failed.Failure;
            }
        }

        try
        {
            return ResolveUncached(projectRoot, locked, new HashSet<string>());
        }
        catch (PackageException ex)
        {
            lock (CacheLock)
            {
                Failures[projectRoot] = (stamp, ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves again, fetching newer commits of git sources instead of staying at the locked ones, and writes the
    /// lock file.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="ids">The packages to update; all of them when null.</param>
    /// <returns>The resolved packages, dependencies first.</returns>
    /// <exception cref="PackageException">A package cannot be fetched or does not fit.</exception>
    public static PackageResolution Update(string projectRoot, IReadOnlySet<string>? ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return ResolveUncached(Path.GetFullPath(projectRoot), locked: false, ids);
    }

    static PackageResolution ResolveUncached(string projectRoot, bool locked, IReadOnlySet<string>? update)
    {

        var resolution = NewResolver(locked, update).ResolveAsync(projectRoot).GetAwaiter().GetResult();
        PackageAssetIds.EnsureUnique(Path.Combine(projectRoot, "Assets"), resolution.Packages);
        foreach (var warning in resolution.Warnings)
        {
            Log.Logger.LogWarning("{Warning}", warning);
        }

        if (resolution.UsesUserOverride)
        {
            Log.Logger.LogInformation("Packages resolved with personal overrides; the lock file is left unchanged");
        }
        else if (!locked && Directory.Exists(Path.Combine(projectRoot, ProjectManifest.DirectoryName)))
        {
            resolution.Lock.Save(projectRoot);
        }

        lock (CacheLock)
        {
            Cache[projectRoot] = (Stamp(projectRoot, locked), resolution);
            _ = Failures.Remove(projectRoot);
        }

        return resolution;
    }

    /// <summary>Fetches one brick from a source without resolving its dependencies, for comparing against it.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="source">Where it comes from.</param>
    /// <returns>The brick's folder and what pins it.</returns>
    /// <exception cref="PackageException">The source cannot be fetched or holds another brick.</exception>
    public static FetchedPackage Fetch(string id, PackageSource source) =>
        NewResolver(locked: false, update: new HashSet<string>()).FetchAsync(id, source).GetAwaiter().GetResult();

    /// <summary>Forgets the cached resolution of a project, for a caller that has just rewritten its manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public static void Invalidate(string projectRoot)
    {
        lock (CacheLock)
        {
            _ = Cache.Remove(Path.GetFullPath(projectRoot));
            _ = Failures.Remove(Path.GetFullPath(projectRoot));
        }
    }

    /// <summary>
    /// The package folders of a project, dependencies first: the <see cref="DefaultDependencies"/> even when it
    /// declares no packages; none for a folder that is not a project.
    /// </summary>
    /// <param name="projectRoot">The project folder, or empty for none.</param>
    /// <returns>The resolved packages.</returns>
    public static IReadOnlyList<ResolvedPackage> ResolveOrEmpty(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(Path.Combine(projectRoot, "Assets")))
        {
            return [];
        }

        ProjectManifest.MigrateLegacyLayout(projectRoot);
        try
        {
            return Resolve(projectRoot).Packages;
        }
        catch (PackageException ex)
        {
            // The editor's callers each note what they leave out; what is missing and why is told here, once per failure.
            if (Reported.TryAdd(ex, Reported))
            {
                Log.Logger.LogError("The project's bricks could not be resolved: {Problem}", ex.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Records that the project leaves out a default brick it no longer declares, so it is not added back.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    internal static void OptOutOfDefault(string projectRoot, string id)
    {
        if (!DefaultDependencies.ContainsKey(id))
        {
            return;
        }

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        manifest.Dependencies[id] = null;
        manifest.Save(projectRoot);
        Invalidate(projectRoot);
    }

    /// <summary>
    /// The public registry every project can take bricks from, accepting all brick names and trusted with
    /// MASS4's signing key. Its name is what lock files record, so it stays fixed while the asset store's address moves.
    /// </summary>
    public static ScopedRegistry PublicRegistry { get; } = new()
    {
        Name = "bricks.mass4.org",
        Url = "https://bricks.mass4.org/v1",
        Mirrors = ["https://store.mass4.org/v1"],
        Keys = [ReadEmbeddedKey()],
    };

    static string ReadEmbeddedKey()
    {
        using var stream = typeof(ProjectPackages).Assembly.GetManifestResourceStream("registry-mass4.pub")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    static PackageResolver NewResolver(bool locked, IReadOnlySet<string>? update) =>
        new(new PackageStore(PackageStore.DefaultRoot()), new PackageResolverOptions
        {
            Registries = [PublicRegistry],
            Hosts = new Dictionary<string, SemanticVersion> { [HostName] = EngineVersion },
            BuiltinDirectory = BuiltinDirectory,
            ReservedCategoryPrefixes = ["gaya", HostName],
            Locked = locked,
            Update = update,
            DefaultDependencies = DefaultDependencies,
        });

    static string FindBuiltinDirectory()
    {
        // Beside the assembly, not AppContext.BaseDirectory: the turian-cli/turian-studio launchers load the engine from lib/.
        var published = Path.Combine(Path.GetDirectoryName(typeof(ProjectPackages).Assembly.Location) ?? AppContext.BaseDirectory, "packages");
        if (Directory.Exists(published))
        {
            return published;
        }

        return typeof(ProjectPackages).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static a => a.Key == "TurianBuiltinPackages")?.Value ?? published;
    }

    static string Stamp(string projectRoot, bool locked)
    {
        var directory = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var files = new[] { ProjectManifest.FileName, ProjectManifest.UserFileName, ProjectManifest.LinksFileName, LockFile.FileName }
            .Select(name => Path.Combine(directory, name))
            .Select(static path => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        IEnumerable<string> embedded = Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            : [];

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: !locked);
        var links = manifest.Links.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var path = Path.Combine(Path.GetFullPath(pair.Value, directory), PackageManifest.FileName);
            return $"{pair.Key}:{(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0)}";
        });
        return $"{locked}|{string.Join('|', files)}|{string.Join('|', embedded)}|{string.Join('|', links)}";
    }

    static SemanticVersion ReadVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var version))
        {
            return version;
        }

        var fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemanticVersion(fallback.Major, fallback.Minor, Math.Max(fallback.Build, 0));
    }
}
