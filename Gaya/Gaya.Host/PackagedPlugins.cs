using Gaya.Packages;

namespace Gaya.Host;

/// <summary>
/// Plugins installed into the application itself rather than into a project: the packages of the per-user
/// studio manifest (<c>~/.gaya/studio/Bricks/manifest.json</c>) whose scopes include
/// <see cref="PackageScope.Studio"/>. Each ships its compiled plugin assemblies in its <c>Precast~/lib</c> folder.
/// </summary>
public static class PackagedPlugins
{
    /// <summary>The folder holding the studio manifest, lock file and embedded studio packages.</summary>
    public static string StudioRoot => Path.Combine(UserConfigPath.Directory, "studio");

    /// <summary>The folder, inside a package, holding its compiled plugin assemblies.</summary>
    public const string LibraryDirectoryName = "Precast~/lib";

    /// <summary>
    /// Resolves the studio packages and loads their plugin assemblies. A package that fails to resolve or load is
    /// logged and left out, like a plugin that throws while configuring: the application still starts.
    /// </summary>
    /// <param name="hosts">Host → version, checked against each package's <c>engines</c>.</param>
    /// <param name="logger">Where failures are reported.</param>
    /// <param name="studioRoot">The studio folder; <see cref="StudioRoot"/> when null.</param>
    /// <returns>The loaded assemblies, dependencies first.</returns>
    public static IReadOnlyList<Assembly> Load(IReadOnlyDictionary<string, SemanticVersion> hosts, ILogger logger,
        string? studioRoot = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(logger);
        studioRoot ??= StudioRoot;
        ProjectManifest.MigrateLegacyLayout(studioRoot);
        if (!File.Exists(Path.Combine(studioRoot, ProjectManifest.DirectoryName, ProjectManifest.FileName))) return [];

        PackageResolution resolution;
        try
        {
            var store = new PackageStore(PackageStore.DefaultRoot());
            resolution = new PackageResolver(store, new PackageResolverOptions
            {
                Hosts = hosts,
                Scope = PackageScope.Studio,
                ReservedCategoryPrefixes = ["gaya", .. hosts.Keys],
            }).ResolveAsync(studioRoot).GetAwaiter().GetResult();
        }
        catch (PackageException ex)
        {
            logger.LogError(ex, "Studio packages could not be resolved; none are loaded");
            return [];
        }

        if (!resolution.UsesUserOverride) resolution.Lock.Save(studioRoot);

        var assemblies = new List<Assembly>();
        foreach (var package in resolution.Packages)
        {
            var library = Path.Combine(package.RootPath, LibraryDirectoryName);
            if (!Directory.Exists(library)) continue;

            foreach (var path in Directory.EnumerateFiles(library, "*.dll").Order(StringComparer.Ordinal))
            {
                try
                {
                    assemblies.Add(System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
                {
                    logger.LogError(ex, "Studio package {Package}: {Assembly} could not be loaded", package.Id, path);
                }
            }

            logger.LogInformation("Studio package {Package} {Version} loaded", package.Id, package.Version);
        }

        return assemblies;
    }
}
