using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>A build profile found in a project, with the name builds refer to it by.</summary>
/// <param name="Name">The profile's <see cref="BuildProfile.Name"/>, else its file name.</param>
/// <param name="Path">The profile's data-asset file.</param>
/// <param name="Profile">The profile.</param>
public sealed record ProjectBuildProfile(string Name, string Path, BuildProfile Profile);

/// <summary>A build profile the project asset registers, as the Build Profiles window lists it.</summary>
/// <param name="Id">The profile's asset id.</param>
/// <param name="Profile">The profile with its name and file.</param>
/// <param name="IsActive">Whether Build &amp; Run, exports and play mode follow it.</param>
public sealed record BuildProfileListing(Guid Id, ProjectBuildProfile Profile, bool IsActive);

/// <summary>Finds a project's build profiles and decides which bricks each ships.</summary>
public static class BuildProfiles
{
    static readonly Guid ProfileTypeId = Guid.Parse(BuildProfile.TypeIdValue);

    /// <summary>
    /// The build profiles the project's <see cref="ProjectAsset"/> registers, in its order; a project not yet given
    /// one has every profile under its <c>Assets</c> folder, by name.
    /// </summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The profiles; none when the project declares none.</returns>
    public static IReadOnlyList<ProjectBuildProfile> Discover(IBuildAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Directory.Exists(settings.AssetsAbsoluteDir))
        {
            return [];
        }

        if (ProjectAsset.Load(settings.AssetsAbsoluteDir) is not { } project)
        {
            return Search(settings.AssetsAbsoluteDir);
        }

        var index = new AssetSourceIndex(settings.ProjectAbsoluteDir);
        return [.. project.BuildProfiles.Where(static r => !r.IsEmpty)
            .Select(r => index.PathOf(r.AssetId))
            .OfType<string>()
            .Select(path => Read(path, index))
            .OfType<ProjectBuildProfile>()];
    }

    /// <summary>
    /// The build profiles the project's <see cref="ProjectAsset"/> registers, in its order, each with its id and
    /// whether it is the active one; profiles whose file is gone or unreadable are left out.
    /// </summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The profiles; none when the project has no project asset.</returns>
    public static IReadOnlyList<BuildProfileListing> List(IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ProjectAsset.Load(settings.AssetsAbsoluteDir) is not { } project)
        {
            return [];
        }

        var index = new AssetSourceIndex(settings.ProjectAbsoluteDir);
        var active = project.ActiveBuildProfile?.AssetId;
        return [.. project.BuildProfiles.Where(static r => !r.IsEmpty).Select(static r => r.AssetId).Distinct()
            .Select(id => index.PathOf(id) is { } path && Read(path, index) is { } profile
                ? new BuildProfileListing(id, profile, id == active)
                : null)
            .OfType<BuildProfileListing>()];
    }

    /// <summary>The profile the project's <see cref="ProjectAsset"/> marks active.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The profile, or <c>null</c> when none is active or it cannot be read.</returns>
    public static ProjectBuildProfile? Active(IBuildAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ProjectAsset.Load(settings.AssetsAbsoluteDir)?.ActiveBuildProfile is not { IsEmpty: false } active)
        {
            return null;
        }

        var index = new AssetSourceIndex(settings.ProjectAbsoluteDir);
        return index.PathOf(active.AssetId) is { } path ? Read(path, index) : null;
    }

    /// <summary>
    /// The script symbols of the active profile, which the editor compiles scripts with and the IDE reads them
    /// with, as Unity's active build profile defines.
    /// </summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The symbols; none when no profile is active.</returns>
    public static IReadOnlyList<string> ActiveSymbols(IBuildAppSettings settings) =>
        Active(settings)?.Profile.Symbols ?? [];

    /// <summary>
    /// Every build profile under <paramref name="assetsDirectory"/>, by name: what migrating a project registers.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The profiles found.</returns>
    public static IReadOnlyList<ProjectBuildProfile> Search(string assetsDirectory)
    {
        var index = new AssetSourceIndex(Path.GetDirectoryName(Path.GetFullPath(assetsDirectory))!);
        return [.. ProjectSettingsLoader.DataAssetSources(assetsDirectory)
            .Where(static path => AssemblyGraph.PeekTypeId(path) == ProfileTypeId)
            .Select(path => Read(path, index))
            .OfType<ProjectBuildProfile>()
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // Through the index, so a profile can be a variant of another.
    static ProjectBuildProfile? Read(string path, AssetSourceIndex index) =>
        index.LoadFile(path) is BuildProfile profile
            ? new ProjectBuildProfile(
                string.IsNullOrWhiteSpace(profile.Name)
                    ? Path.GetFileNameWithoutExtension(path)
                    : profile.Name.Trim(),
                path, profile)
            : null;

    /// <summary>The profile named <paramref name="name"/>, ignoring case.</summary>
    /// <param name="settings">The project settings.</param>
    /// <param name="name">A profile name.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="InvalidOperationException">
    /// No profile has that name; the message lists the ones that exist.
    /// </exception>
    public static ProjectBuildProfile Find(IBuildAppSettings settings, string name)
    {
        var profiles = Discover(settings);
        return profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(profiles.Count == 0
                   ? $"The project has no build profile named '{name}'; it declares none."
                   : $"The project has no build profile named '{name}'. "
                     + $"Profiles: {string.Join(", ", profiles.Select(static p => p.Name))}.");
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
        if (target == BuildTarget.Client)
        {
            return shipped;
        }

        // Resolution lists dependencies first, so one pass sees every dependency's verdict before its dependents.
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var brick in shipped)
        {
            if (brick.Manifest.ClientOnly || brick.Manifest.Dependencies.Keys.Any(excluded.Contains))
            {
                excluded.Add(brick.Id);
            }
        }

        return [.. shipped.Where(b => !excluded.Contains(b.Id))];
    }

    /// <summary>Fails an export when a shipped brick does not list <paramref name="platform"/> among its platforms.</summary>
    /// <param name="shipped">The bricks the build ships.</param>
    /// <param name="platform">The build platform's id.</param>
    /// <exception cref="InvalidOperationException">A shipped brick does not support the platform; names every one.</exception>
    public static void EnsureSupported(IEnumerable<ResolvedPackage> shipped, string platform)
    {
        var unsupported = shipped.Where(b => !b.Manifest.SupportsPlatform(platform)).Select(static b => b.Id).ToList();
        if (unsupported.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {platform} platform cannot ship these bricks: {string.Join(", ", unsupported)}. "
                + "Remove them from the project or build for a platform they support.");
        }
    }

    /// <summary>
    /// Whether any of <paramref name="bricks"/> provides a game player, which a client build needs to open a window.
    /// </summary>
    /// <param name="bricks">The shipped bricks.</param>
    /// <returns>True when a client-only brick, such as the renderer, is among them.</returns>
    public static bool HasPlayer(IEnumerable<ResolvedPackage> bricks) =>
        bricks.Any(static b => b.Manifest.ClientOnly);
}
