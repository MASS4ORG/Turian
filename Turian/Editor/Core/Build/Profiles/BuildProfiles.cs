using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>A build profile found in a project, with the name builds refer to it by.</summary>
/// <param name="Name">The profile's <see cref="BuildProfile.Name"/>, else its file name.</param>
/// <param name="Path">The profile's data-asset file.</param>
/// <param name="Profile">The profile.</param>
public sealed record ProjectBuildProfile(string Name, string Path, BuildProfile Profile);

/// <summary>Finds a project's build profiles and decides which bricks each ships.</summary>
public static class BuildProfiles
{
    static readonly Guid ProfileTypeId = Guid.Parse(BuildProfile.TypeIdValue);

    /// <summary>The build profiles under the project's <c>Assets</c> folder, in name order.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The profiles; none when the project declares none.</returns>
    public static IReadOnlyList<ProjectBuildProfile> Discover(IBuildAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Directory.Exists(settings.AssetsAbsoluteDir)) return [];

        return [.. ProjectSettingsLoader.DataAssetSources(settings.AssetsAbsoluteDir)
            .Where(static path => AssemblyGraph.PeekTypeId(path) == ProfileTypeId)
            .Select(static path => (Path: path, Profile: DataAsset.LoadContent(path, database: null) as BuildProfile))
            .Where(static entry => entry.Profile is not null)
            .Select(static entry => new ProjectBuildProfile(
                string.IsNullOrWhiteSpace(entry.Profile!.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(entry.Path)
                    : entry.Profile.Name.Trim(),
                entry.Path, entry.Profile))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The profile named <paramref name="name"/>, ignoring case.</summary>
    /// <param name="settings">The project settings.</param>
    /// <param name="name">A profile name.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="InvalidOperationException">No profile has that name; the message lists the ones that exist.</exception>
    public static ProjectBuildProfile Find(IBuildAppSettings settings, string name)
    {
        var profiles = Discover(settings);
        return profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(profiles.Count == 0
                   ? $"The project has no build profile named '{name}'; it declares none."
                   : $"The project has no build profile named '{name}'. Profiles: {string.Join(", ", profiles.Select(static p => p.Name))}.");
    }

    /// <summary>
    /// The installed bricks a <paramref name="target"/> build ships: those a game runs with, less, for a server,
    /// every client-only brick and every brick depending on one.
    /// </summary>
    /// <param name="bricks">The project's resolved bricks.</param>
    /// <param name="target">What the build runs as.</param>
    /// <returns>The shipped bricks, in their resolution order.</returns>
    public static IReadOnlyList<ResolvedPackage> ShippingBricks(IEnumerable<ResolvedPackage> bricks, BuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        var shipped = bricks.Where(static b => !b.Manifest.EditorOnly && !b.Manifest.CompileTimeOnly).ToList();
        if (target == BuildTarget.Client) return shipped;

        // Resolution lists dependencies first, so one pass sees every dependency's verdict before its dependents.
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var brick in shipped)
        {
            if (brick.Manifest.ClientOnly || brick.Manifest.Dependencies.Keys.Any(excluded.Contains))
                excluded.Add(brick.Id);
        }

        return [.. shipped.Where(b => !excluded.Contains(b.Id))];
    }

    /// <summary>Whether any of <paramref name="bricks"/> provides a game player, which a client build needs to open a window.</summary>
    /// <param name="bricks">The shipped bricks.</param>
    /// <returns>True when a client-only brick, such as the renderer, is among them.</returns>
    public static bool HasPlayer(IEnumerable<ResolvedPackage> bricks) =>
        bricks.Any(static b => b.Manifest.ClientOnly);
}
