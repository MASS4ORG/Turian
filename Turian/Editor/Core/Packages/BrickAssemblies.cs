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

    /// <summary>The file, inside <c>Precast~</c>, listing the types of the prebuilt assemblies with their type ids.</summary>
    public const string TypesFile = "types.json";

    static readonly Lock LoadLock = new();

    /// <summary>
    /// Whether the brick has a usable prebuilt payload: assemblies in <c>Precast~</c> that were not compiled against
    /// another major or minor version of the engine.
    /// </summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>True when consumers use the payload instead of compiling the brick's sources.</returns>
    public static bool IsPrecast(ResolvedPackage brick) =>
        RuntimeAssemblies(brick).Count > 0 || EditorAssemblies(brick).Count > 0;

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
            var installed = bricks.Where(static b => !b.Manifest.CompileTimeOnly).ToList();
            foreach (var path in installed.SelectMany(static b => RuntimeAssemblies(b).Concat(EditorAssemblies(b))))
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

            foreach (var brick in installed) RegisterTypes(brick, logger);
        }

        return loadedAny;
    }

    /// <summary>The type entries of a brick's prebuilt assemblies, which name their classes and type ids.</summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>The entries; none when the brick has no <see cref="TypesFile"/>.</returns>
    public static IReadOnlyList<UserCodeTypeEntry> PrecastTypes(ResolvedPackage brick)
    {
        var path = Path.Combine(brick.RootPath, PrecastFolder, TypesFile);
        if (!File.Exists(path) || !IsPrecast(brick)) return [];

        try
        {
            return JsonSerializer.Deserialize<UserCodeTypeManifest>(File.ReadAllText(path))?.Types ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    static void RegisterTypes(ResolvedPackage brick, ILogger logger)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var entry in PrecastTypes(brick))
        {
            if (TypeRegistry.TryGetType(entry.TypeId, out _)) continue;

            var type = assemblies.Where(a => entry.Assembly is null || a.GetName().Name == entry.Assembly)
                .Select(a => a.GetType(entry.FullyQualifiedName)).FirstOrDefault(static t => t is not null);
            if (type is null) logger.LogWarning("Brick {Brick}: type {Type} is not in its prebuilt assemblies", brick.Id, entry.FullyQualifiedName);
            else TypeRegistry.Register(entry.TypeId, type);
        }
    }

    static string[] Find(ResolvedPackage brick, string folder)
    {
        var directory = Path.Combine(brick.RootPath, PrecastFolder, folder);
        return Directory.Exists(directory) && BuiltForThisEngine(brick)
            ? [.. Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal)]
            : [];
    }

    static bool BuiltForThisEngine(ResolvedPackage brick) =>
        brick.Manifest.Precast is not { } precast
        || !precast.BuiltWith.TryGetValue(ProjectPackages.HostName, out var built)
        || (built.Major == ProjectPackages.EngineVersion.Major && built.Minor == ProjectPackages.EngineVersion.Minor);
}
