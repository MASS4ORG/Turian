namespace Gaya.Host;

/// <summary>The studio's resolved bricks and the plugin assemblies loaded from them.</summary>
/// <param name="Packages">Every resolved studio brick, dependencies first, content-only ones included.</param>
/// <param name="Assemblies">The plugin assemblies loaded from the bricks' <c>Precast~/lib</c> folders.</param>
public sealed record StudioPackages(IReadOnlyList<ResolvedPackage> Packages, IReadOnlyList<Assembly> Assemblies)
{
    /// <summary>Nothing resolved or loaded.</summary>
    public static StudioPackages Empty { get; } = new([], []);
}

/// <summary>
/// Bricks installed into the application itself rather than into a project: the packages of the per-user studio
/// manifest (<c>~/.gaya/studio/Bricks/manifest.json</c>) whose scopes include <see cref="PackageScope.Studio"/>. A
/// brick may ship compiled plugin assemblies in its <c>Precast~/lib</c> folder, content such as themes, or both.
/// </summary>
public static class PackagedPlugins
{
    /// <summary>The folder holding the studio manifest, lock file and embedded studio packages.</summary>
    public static string StudioRoot => GayaConfig.StudioRoot;

    /// <summary>The folder, inside a package, holding its compiled plugin assemblies.</summary>
    public const string LibraryDirectoryName = StudioBricks.LibraryDirectoryName;

    /// <summary>The hosts a studio without an engine checks bricks against: Gaya itself.</summary>
    public static IReadOnlyDictionary<string, SemanticVersion> DefaultHosts { get; } =
        new Dictionary<string, SemanticVersion> { ["gaya"] = GayaVersion() };

    /// <summary>The studio's bricks as a workspace the Bricks panel and the command line manage.</summary>
    /// <param name="hosts">Host → version, checked against each brick's <c>engines</c>.</param>
    /// <param name="studioRoot">The studio folder; <see cref="StudioRoot"/> when null.</param>
    /// <param name="publicRegistry">The registry every studio takes bricks from, or null.</param>
    /// <returns>The workspace.</returns>
    public static StudioBricks Workspace(IReadOnlyDictionary<string, SemanticVersion> hosts, string? studioRoot = null,
        ScopedRegistry? publicRegistry = null) =>
        new(studioRoot ?? StudioRoot, hosts, publicRegistry: publicRegistry);

    /// <summary>
    /// Resolves the studio bricks and loads their plugin assemblies. A brick that fails to resolve or load is logged and
    /// left out, like a plugin that throws while configuring: the application still starts.
    /// </summary>
    /// <param name="hosts">Host → version, checked against each brick's <c>engines</c>.</param>
    /// <param name="logger">Where failures are reported.</param>
    /// <param name="studioRoot">The studio folder; <see cref="StudioRoot"/> when null.</param>
    /// <returns>The resolved bricks and loaded assemblies, dependencies first.</returns>
    public static StudioPackages Load(IReadOnlyDictionary<string, SemanticVersion> hosts, ILogger logger,
        string? studioRoot = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(logger);

        PackageResolution resolution;
        try
        {
            resolution = Workspace(hosts, studioRoot).ResolveAll();
        }
        catch (PackageException ex)
        {
            logger.LogError(ex, "Studio packages could not be resolved; none are loaded");
            return StudioPackages.Empty;
        }

        var assemblies = new List<Assembly>();
        foreach (var package in resolution.Packages)
        {
            assemblies.AddRange(LoadAssemblies(package, logger));
            logger.LogInformation("Studio package {Package} {Version} loaded", package.Id, package.Version);
        }

        return new StudioPackages(resolution.Packages, assemblies);
    }

    /// <summary>The <c>Themes</c> folders of the studio bricks that provide themes, icon themes or font packs.</summary>
    /// <param name="packages">The resolved studio bricks.</param>
    /// <returns>The existing folders, in resolution order.</returns>
    public static IReadOnlyList<string> ThemeFolders(IEnumerable<ResolvedPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        string[] categories = [ThemeCategories.ColorTheme, ThemeCategories.IconTheme, ThemeCategories.FontPack];
        return
        [
            .. packages.Where(package => package.Manifest.Categories.Any(categories.Contains))
                .Select(package => Path.Combine(package.RootPath, "Themes"))
                .Where(Directory.Exists),
        ];
    }

    static IEnumerable<Assembly> LoadAssemblies(ResolvedPackage package, ILogger logger)
    {
        var library = Path.Combine(package.RootPath, LibraryDirectoryName);
        if (!Directory.Exists(library)) yield break;

        foreach (var path in Directory.EnumerateFiles(library, "*.dll").Order(StringComparer.Ordinal))
        {
            Assembly? assembly = null;
            try
            {
                assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                logger.LogError(ex, "Studio package {Package}: {Assembly} could not be loaded", package.Id, path);
            }

            if (assembly is not null) yield return assembly;
        }
    }

    static SemanticVersion GayaVersion()
    {
        var assembly = typeof(PackageResolver).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var version)) return version;
        var fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemanticVersion(fallback.Major, fallback.Minor, Math.Max(fallback.Build, 0));
    }
}
