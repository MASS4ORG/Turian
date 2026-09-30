using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// The prebuilt assemblies a brick ships: <c>Precast~/lib</c> for what games run and the editor loads, and
/// <c>Precast~/editor</c> for what only the editor loads.
/// </summary>
public static class BrickAssemblies
{
    /// <summary>The folder, at a brick's root, that holds its prebuilt payload.</summary>
    public const string PrecastFolder = "Precast~";

    static readonly Lock LoadLock = new();

    /// <summary>The assemblies games run with, which user code compiles against.</summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>Absolute assembly paths, in name order.</returns>
    public static IReadOnlyList<string> RuntimeAssemblies(ResolvedPackage brick) => Find(brick, "lib");

    /// <summary>The assemblies only the editor loads, such as importers.</summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>Absolute assembly paths, in name order.</returns>
    public static IReadOnlyList<string> EditorAssemblies(ResolvedPackage brick) => Find(brick, "editor");

    /// <summary>
    /// Loads the prebuilt assemblies of <paramref name="bricks"/> into the editor and registers their
    /// <see cref="TypeIdAttribute"/> types. An assembly the process already has, such as the host's own copy of a
    /// shared library, is kept.
    /// </summary>
    /// <param name="bricks">The installed bricks.</param>
    /// <param name="logger">Receives assemblies that cannot be loaded.</param>
    /// <returns>Whether any assembly was newly loaded.</returns>
    public static bool Load(IEnumerable<ResolvedPackage> bricks, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        ArgumentNullException.ThrowIfNull(logger);

        var loadedAny = false;
        lock (LoadLock)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(static a => a.GetName().Name).ToHashSet();
            foreach (var path in bricks.Where(static b => !b.Manifest.CompileTimeOnly)
                         .SelectMany(static b => RuntimeAssemblies(b).Concat(EditorAssemblies(b))))
            {
                try
                {
                    if (!loaded.Add(AssemblyName.GetAssemblyName(path).Name)) continue;

                    TypeRegistry.ScanAssembly(AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
                    loadedAny = true;
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
                {
                    logger.LogWarning(ex, "Could not load brick assembly {Path}", path);
                }
            }
        }

        return loadedAny;
    }

    static string[] Find(ResolvedPackage brick, string folder)
    {
        var directory = Path.Combine(brick.RootPath, PrecastFolder, folder);
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal)]
            : [];
    }
}
