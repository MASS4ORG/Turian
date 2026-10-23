using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>
/// The prebuilt assemblies a brick ships: <c>Precast~/lib</c> for what games run and the editor loads, and
/// <c>Precast~/editor</c> for what only the editor loads.
/// </summary>
public static class BrickAssemblies
{
    /// <summary>The folder, at a v1 brick's root, that holds its prebuilt payload; v2 bricks name it by role.</summary>
    public const string PrecastFolder = "Precast~";

    /// <summary>A brick's prebuilt payload folder: <see cref="PrecastFolder"/>, or the v2 <c>precast</c> role.</summary>
    /// <param name="brick">The resolved brick.</param>
    /// <returns>The folder; it may not exist.</returns>
    public static string PrecastDirectory(ResolvedPackage brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        return BrickLayout.PathOf(brick.RootPath, brick.Manifest, BrickLayout.Precast);
    }

    /// <summary>
    /// The prebuilt payload folder of the brick folder at <paramref name="brickRoot"/>; <see cref="PrecastFolder"/>
    /// for a folder without a manifest.
    /// </summary>
    /// <param name="brickRoot">The brick folder.</param>
    /// <returns>The folder; it may not exist.</returns>
    public static string PrecastDirectory(string brickRoot) =>
        File.Exists(Path.Combine(brickRoot, PackageManifest.FileName))
            ? BrickLayout.PathOf(brickRoot, PackageManifest.Load(brickRoot, ["gaya", ProjectPackages.HostName]), BrickLayout.Precast)
            : Path.Combine(brickRoot, PrecastFolder);

    /// <summary>All prebuilt payload folders in the manifest's declared order.</summary>
    /// <param name="brick">The resolved brick.</param>
    /// <returns>The payload folders.</returns>
    public static IReadOnlyList<string> PrecastDirectories(ResolvedPackage brick) =>
        BrickLayout.PathsOf(brick.RootPath, brick.Manifest, BrickLayout.Precast);

    /// <summary>All prebuilt payload folders of a brick, or the legacy folder when no manifest exists.</summary>
    /// <param name="brickRoot">The brick folder.</param>
    /// <returns>The payload folders.</returns>
    public static IReadOnlyList<string> PrecastDirectories(string brickRoot) =>
        File.Exists(Path.Combine(brickRoot, PackageManifest.FileName))
            ? BrickLayout.PathsOf(brickRoot, PackageManifest.Load(brickRoot, ["gaya", ProjectPackages.HostName]), BrickLayout.Precast)
            : [Path.Combine(brickRoot, PrecastFolder)];

    /// <summary>The file, inside <c>Precast~</c>, listing the types of the prebuilt assemblies with their type ids.</summary>
    public const string TypesFile = "types.json";

    static readonly Lock LoadLock = new();
    static readonly Lock ProbeLock = new();
    static readonly HashSet<string> ProbeDirectories = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AssemblyDependencyResolver> Resolvers = new(StringComparer.Ordinal);
    static bool ResolvingRegistered;

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
    /// <param name="projectRoot">The project folder; its bricks' NuGet packages are restored and probed when given.</param>
    /// <returns>Whether any assembly was newly loaded.</returns>
    public static bool Load(IEnumerable<ResolvedPackage> bricks, ILogger logger, string? projectRoot = null)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        ArgumentNullException.ThrowIfNull(logger);

        var loadedAny = false;
        lock (LoadLock)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(static a => a.GetName().Name).ToHashSet();
            var installed = bricks.Where(static b => !b.Manifest.CompileTimeOnly).ToList();
            if (projectRoot is not null)
            {
                RegisterProbing(installed, BrickNuGetRestore.EnsureRestored(installed, projectRoot, logger));
            }
            foreach (var path in installed.SelectMany(static b => RuntimeAssemblies(b).Concat(EditorAssemblies(b))))
            {
                try
                {
                    if (!loaded.Add(AssemblyName.GetAssemblyName(path).Name))
                    {
                        continue;
                    }

                    TypeRegistry.ScanAssembly(AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
                    loadedAny = true;
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
                {
                    logger.LogWarning(ex, "Could not load brick assembly {Path}", path);
                }
            }

            foreach (var brick in installed)
            {
                RegisterTypes(brick, logger);
            }
        }

        return loadedAny;
    }

    /// <summary>The type entries of a brick's prebuilt assemblies, which name their classes and type ids.</summary>
    /// <param name="brick">The installed brick.</param>
    /// <returns>The entries; none when the brick has no <see cref="TypesFile"/>.</returns>
    public static IReadOnlyList<UserCodeTypeEntry> PrecastTypes(ResolvedPackage brick)
    {
        if (!IsPrecast(brick))
        {
            return [];
        }

        var entries = new List<UserCodeTypeEntry>();
        foreach (var path in PrecastDirectories(brick).Select(static directory => Path.Combine(directory, TypesFile)))
        {
            entries.AddRange(ReadTypes(path));
        }

        return entries.DistinctBy(static entry => entry.TypeId).ToList();
    }

    static IReadOnlyList<UserCodeTypeEntry> ReadTypes(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
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
            if (TypeRegistry.TryGetType(entry.TypeId, out _))
            {
                continue;
            }

            var type = assemblies.Where(a => entry.Assembly is null || a.GetName().Name == entry.Assembly)
                .Select(a => a.GetType(entry.FullyQualifiedName)).FirstOrDefault(static t => t is not null);
            if (type is null)
            {
                logger.LogWarning("Brick {Brick}: type {Type} is not in its prebuilt assemblies", brick.Id, entry.FullyQualifiedName);
            }
            else
            {
                TypeRegistry.Register(entry.TypeId, type);
            }
        }
    }

    /// <summary>
    /// Resolves a dependency of a loaded brick assembly: first through each brick's
    /// <see cref="AssemblyDependencyResolver"/>, which sees its sidecar <c>.deps.json</c>, then by file name in the
    /// probed brick and restored package folders.
    /// </summary>
    /// <param name="name">The requested assembly name.</param>
    /// <returns>The assembly path, or null when nothing probed holds it.</returns>
    internal static string? ResolveDependency(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (ProbeLock)
        {
            foreach (var resolver in Resolvers.Values)
            {
                if (resolver.ResolveAssemblyToPath(name) is { } resolved && File.Exists(resolved))
                {
                    return resolved;
                }
            }

            foreach (var directory in ProbeDirectories)
            {
                if (name.Name is { } simple && Path.Combine(directory, $"{simple}.dll") is { } candidate && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Remembers where brick dependencies resolve from: each brick's prebuilt folders and resolver, and the
    /// restored package folders. Later brick loads reuse them; assemblies stay loaded until the editor restarts.
    /// </summary>
    /// <param name="bricks">The loaded bricks.</param>
    /// <param name="packageDirectories">The restored package folders.</param>
    internal static void RegisterProbing(IReadOnlyList<ResolvedPackage> bricks, IReadOnlyList<string> packageDirectories)
    {
        lock (ProbeLock)
        {
            if (!ResolvingRegistered)
            {
                AssemblyLoadContext.Default.Resolving += static (_, args) =>
                    args.Name is { } name && ResolveDependency(new AssemblyName(name)) is { } path ? LoadFromProbedPath(path) : null;
                ResolvingRegistered = true;
            }

            foreach (var brick in bricks)
            {
                RegisterBrickProbing(brick);
            }

            foreach (var package in packageDirectories)
            {
                RegisterPackageProbing(package);
            }
        }
    }

    static void RegisterBrickProbing(ResolvedPackage brick)
    {
        foreach (var directory in PrecastDirectories(brick)
                     .SelectMany(static payload => new[] { Path.Combine(payload, "lib"), Path.Combine(payload, "editor") }))
        {
            if (BuiltForThisEngine(brick) && Directory.Exists(directory))
            {
                _ = ProbeDirectories.Add(directory);
            }
        }
        foreach (var path in RuntimeAssemblies(brick).Concat(EditorAssemblies(brick)))
        {
            Resolvers.TryAdd(path, new AssemblyDependencyResolver(path));
        }
    }

    static void RegisterPackageProbing(string package)
    {
        foreach (var folder in new[] { "lib/net10.0", "lib/netstandard2.0", "lib/netstandard2.1" })
        {
            var directory = Path.Combine(package, folder);
            if (Directory.Exists(directory))
            {
                _ = ProbeDirectories.Add(directory);
            }
        }
    }

    static ReflectionAssembly? LoadFromProbedPath(string path)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
        {
            return null;
        }
    }

    static string[] Find(ResolvedPackage brick, string folder)
    {
        return BuiltForThisEngine(brick)
            ? [.. PrecastDirectories(brick).Select(payload => Path.Combine(payload, folder))
                .Where(Directory.Exists).SelectMany(static directory => Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)]
            : [];
    }

    static bool BuiltForThisEngine(ResolvedPackage brick) =>
        brick.Manifest.Precast is not { } precast
        || !precast.BuiltWith.TryGetValue(ProjectPackages.HostName, out var built)
        || (built.Major == ProjectPackages.EngineVersion.Major && built.Minor == ProjectPackages.EngineVersion.Minor);
}
