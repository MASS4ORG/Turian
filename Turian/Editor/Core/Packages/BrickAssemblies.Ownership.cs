using MASS4.Bricks;

namespace Turian.Editor.Core;

public static partial class BrickAssemblies
{
    static readonly Lock OwnershipLock = new();
    static readonly HashSet<string> GameAssemblies = new(StringComparer.Ordinal);
    static readonly ConditionalWeakTable<ReflectionAssembly, string> Names = new();
    static HashSet<string>? enabledAssemblies;

    /// <summary>
    /// Records the prebuilt assemblies of game bricks, including those the studio loaded for its own bricks, so editor
    /// discovery can tell when the open project stops enabling them.
    /// </summary>
    /// <param name="bricks">The resolved bricks; only those with the project scope are recorded.</param>
    public static void RecordGameBricks(IEnumerable<ResolvedPackage> bricks)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        var names = bricks.Where(static brick => brick.Manifest.EffectiveScopes.Contains(PackageScope.Project))
            .SelectMany(AssemblyNames).ToList();
        lock (OwnershipLock)
        {
            GameAssemblies.UnionWith(names);
        }
    }

    /// <summary>Makes <paramref name="bricks"/> the open project's enabled bricks; any other game brick is retired.</summary>
    /// <param name="bricks">The open project's resolved bricks, or none when they do not resolve.</param>
    public static void Enable(IEnumerable<ResolvedPackage> bricks)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        var enabled = bricks.ToList();
        RecordGameBricks(enabled);
        var names = enabled.SelectMany(AssemblyNames).ToHashSet(StringComparer.Ordinal);
        lock (OwnershipLock)
        {
            enabledAssemblies = names;
        }
    }

    /// <summary>
    /// Whether <paramref name="assembly"/> belongs to a game brick the open project does not enable. It stays loaded
    /// until the editor restarts, but editor discovery leaves it out.
    /// </summary>
    /// <param name="assembly">A loaded assembly.</param>
    /// <returns>True for a retired brick assembly; false before any project enabled its bricks.</returns>
    public static bool IsRetired(ReflectionAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.IsCollectible)
        {
            return false;
        }

        var name = Names.GetValue(assembly, static loaded => loaded.GetName().Name ?? string.Empty);
        lock (OwnershipLock)
        {
            return enabledAssemblies is { } enabled && GameAssemblies.Contains(name) && !enabled.Contains(name);
        }
    }

    /// <summary>Forgets the open project's bricks, so no brick assembly is retired.</summary>
    internal static void ResetEnablement()
    {
        lock (OwnershipLock)
        {
            enabledAssemblies = null;
        }
    }

    static IEnumerable<string> AssemblyNames(ResolvedPackage brick) =>
        RuntimeAssemblies(brick).Concat(EditorAssemblies(brick)).Select(Path.GetFileNameWithoutExtension).OfType<string>();
}
