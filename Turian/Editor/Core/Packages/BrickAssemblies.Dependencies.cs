using MASS4.Bricks;

namespace Turian.Editor.Core;

public static partial class BrickAssemblies
{
    // Brick folder -> the dependency that keeps its prebuilt payload from being used.
    static readonly ConcurrentDictionary<string, string> Fallbacks = new(StringComparer.Ordinal);

    /// <summary>
    /// Decides which prebuilt payloads a project may use: a brick is consumed as precast only when every brick it
    /// depends on, transitively, is consumed as precast, is built in or ships no code. Any other brick falls back to
    /// its sources; one without sources is reported as unusable. <see cref="IsPrecast"/> and every assembly lookup
    /// follow the decision.
    /// </summary>
    /// <param name="packages">The project's resolved bricks.</param>
    /// <param name="logger">Receives one warning per brick that cannot use its payload.</param>
    public static void ApplyDependencyRule(IReadOnlyList<ResolvedPackage> packages, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var package in packages)
        {
            _ = Fallbacks.TryRemove(package.RootPath, out _);
        }

        var byId = packages.ToDictionary(static p => p.Id, StringComparer.Ordinal);
        foreach (var twin in packages.Where(static p => p.Manifest.BakeOf is not null))
        {
            _ = byId.TryAdd(twin.Manifest.BakeOf!.Brick, twin);
        }

        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var package in packages.Where(HasPayload))
        {
            if (FindSourceOnlyDependency(package, byId, state) is not { } dependency)
            {
                continue;
            }

            Fallbacks[package.RootPath] = dependency.Id;
            if (HasSources(package))
            {
                logger.LogWarning("Brick {Brick} is compiled from its sources: its dependency {Dependency} has only sources, "
                    + "and a prebuilt brick cannot load before it", package.Id, dependency.Id);
            }
            else
            {
                logger.LogError("Brick {Brick} is unusable: it ships only prebuilt assemblies, which need its dependency "
                    + "{Dependency}, and that has only sources", package.Id, dependency.Id);
            }
        }
    }

    static ResolvedPackage? FindSourceOnlyDependency(
        ResolvedPackage package, Dictionary<string, ResolvedPackage> byId, Dictionary<string, bool> state)
    {
        foreach (var id in package.Manifest.Dependencies.Keys)
        {
            if (byId.TryGetValue(id, out var dependency) && !CanLoadBefore(dependency, byId, state))
            {
                return dependency;
            }
        }

        return null;
    }

    // True when the dependency is available without compiling: built in, consumed as precast, or without code.
    static bool CanLoadBefore(ResolvedPackage package, Dictionary<string, ResolvedPackage> byId, Dictionary<string, bool> state)
    {
        if (package.Origin == PackageOrigin.Builtin)
        {
            return true;
        }

        if (state.TryGetValue(package.Id, out var known))
        {
            return known;
        }

        state[package.Id] = true;
        var result = HasPayload(package)
            ? FindSourceOnlyDependency(package, byId, state) is null
            : !HasSources(package);
        state[package.Id] = result;
        return result;
    }

    // Scripts the project compiles: folders ending in '~' (Brick~ with its tests, tools and payload) are not imported.
    static bool HasSources(ResolvedPackage brick) =>
        Directory.Exists(brick.RootPath)
        && Directory.EnumerateFiles(brick.RootPath, "*.cs", SearchOption.AllDirectories)
            .Any(file => !Path.GetRelativePath(brick.RootPath, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .SkipLast(1).Any(static segment => segment.EndsWith('~')));
}
