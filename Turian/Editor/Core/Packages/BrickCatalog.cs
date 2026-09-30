using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// How far a brick is from being used: known from a repository (<see cref="Available"/>), present on this machine
/// (<see cref="Installed"/>), or used by the project (<see cref="Enabled"/>). The project only lists what it uses.
/// </summary>
public enum BrickState
{
    /// <summary>Offered by the built-in bricks or a registry, and not yet on this machine.</summary>
    Available,

    /// <summary>Downloaded to this machine's shared store, and not used by the project.</summary>
    Installed,

    /// <summary>Used by the project: declared by it, needed by a brick it uses, or a local brick in its folder.</summary>
    Enabled,
}

/// <summary>What the Bricks panel narrows its list to.</summary>
public enum BrickFilter
{
    /// <summary>Every brick.</summary>
    All,

    /// <summary>Installed bricks, enabled or not.</summary>
    Installed,

    /// <summary>Bricks not yet installed.</summary>
    Available,

    /// <summary>Installed bricks with a newer version available.</summary>
    Updates,

    /// <summary>Bricks that ship with the engine.</summary>
    BuiltIn,
}

/// <summary>One brick as the catalog lists it, whether or not the project installs it.</summary>
/// <param name="Id">The brick id.</param>
/// <param name="DisplayName">The name shown to users, or null.</param>
/// <param name="Description">What the brick does, or null.</param>
/// <param name="Author">Who made it, or null.</param>
/// <param name="State">How far the brick is into the project.</param>
/// <param name="InstalledVersion">The version on this machine or in use, or null when available only.</param>
/// <param name="LatestVersion">The newest version any known source offers.</param>
/// <param name="Origin">
/// Where this client got or would get the brick: what the project declares for an installed brick, else what the
/// client itself recorded or configured. Nothing the brick says about itself counts.
/// </param>
/// <param name="InstallSource">The value to declare to install it; null for a built-in brick or when no origin is known.</param>
/// <param name="IsBuiltin">Whether the brick ships with the engine.</param>
public sealed record CatalogBrick(string Id, string? DisplayName, string? Description, string? Author, BrickState State,
    string? InstalledVersion, string? LatestVersion, string? Origin, string? InstallSource, bool IsBuiltin)
{
    /// <summary>Whether the brick is on this machine or in use.</summary>
    public bool IsInstalled => State != BrickState.Available;

    /// <summary>Whether the newest known version is newer than the one on this machine or in use.</summary>
    public bool HasUpdate => InstalledVersion is { } installed && LatestVersion is { } latest
                             && SemanticVersion.TryParse(installed, out var have) && SemanticVersion.TryParse(latest, out var newest)
                             && newest > have;
}

/// <summary>
/// Merges what a project installs with what its machine and registries offer into one list: available, installed and
/// enabled bricks. Sources are read on the client; each brick's origin is the one this client used, not one the brick
/// claims.
/// </summary>
public static class BrickCatalog
{
    /// <summary>The catalog from sources that need no network: the project, the built-in bricks and the shared store.</summary>
    /// <param name="installed">What the project resolves: every brick it uses.</param>
    /// <param name="builtinDirectory">The folder of the bricks that ship with the engine.</param>
    /// <param name="store">The shared store, or null.</param>
    /// <returns>One entry per brick id, by id.</returns>
    public static IReadOnlyList<CatalogBrick> Local(IReadOnlyList<ResolvedPackage> installed, string? builtinDirectory,
        PackageStore? store)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var bricks = new Dictionary<string, CatalogBrick>(StringComparer.Ordinal);

        foreach (var package in installed)
        {
            bricks[package.Id] = new CatalogBrick(package.Id, package.Manifest.DisplayName, package.Manifest.Description,
                package.Manifest.Author, BrickState.Enabled,
                package.Version.ToString(), package.Version.ToString(), package.Source, package.Source,
                package.Origin == PackageOrigin.Builtin);
        }

        foreach (var manifest in BuiltinBricks(builtinDirectory))
            Offer(bricks, manifest, $"builtin:{manifest.Name}", null, stored: false);

        if (store is not null)
        {
            foreach (var (folder, manifest) in store.Packages(["gaya", ProjectPackages.HostName]))
            {
                var origin = store.ReadOrigin(folder);
                Offer(bricks, manifest, origin, InstallSourceOf(origin, manifest), stored: true);
            }
        }

        return [.. bricks.Values.OrderBy(static b => b.DisplayName ?? b.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Adds what registries offer: bricks not seen yet become available, and newer versions mark updates.</summary>
    /// <param name="bricks">The catalog so far.</param>
    /// <param name="found">The registry, id and newest version of each brick the registries list.</param>
    /// <returns>The merged catalog.</returns>
    public static IReadOnlyList<CatalogBrick> WithRegistries(IReadOnlyList<CatalogBrick> bricks,
        IEnumerable<(string Registry, string Id, string Latest)> found)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        ArgumentNullException.ThrowIfNull(found);

        var merged = bricks.ToDictionary(static b => b.Id, StringComparer.Ordinal);
        foreach (var (registry, id, latest) in found)
        {
            if (!PackageId.IsValid(id) || !SemanticVersion.TryParse(latest, out var version)) continue;

            merged[id] = merged.TryGetValue(id, out var known)
                ? Offered(known, latest, registry, version)
                : new CatalogBrick(id, null, null, null, BrickState.Available, null, latest, $"registry:{registry}",
                    $"^{version}", IsBuiltin: false);
        }

        return [.. merged.Values.OrderBy(static b => b.DisplayName ?? b.Id, StringComparer.OrdinalIgnoreCase)];
    }

    // A brick on the machine whose origin nobody recorded is fetched again from the registry that lists it.
    static CatalogBrick Offered(CatalogBrick known, string latest, string registry, SemanticVersion version)
    {
        var withOrigin = known.InstallSource is null && !known.IsBuiltin && known.State != BrickState.Enabled
            ? known with { Origin = $"registry:{registry}", InstallSource = $"^{version}" }
            : known;
        return Newer(withOrigin, version) ? withOrigin with { LatestVersion = latest } : withOrigin;
    }

    /// <summary>Narrows a catalog to a filter and a search text.</summary>
    /// <param name="bricks">The catalog.</param>
    /// <param name="filter">Which group to keep.</param>
    /// <param name="search">Text the id, name or author must contain; everything when empty.</param>
    /// <returns>The matching bricks, in order.</returns>
    public static IReadOnlyList<CatalogBrick> Filter(IEnumerable<CatalogBrick> bricks, BrickFilter filter, string? search)
    {
        ArgumentNullException.ThrowIfNull(bricks);

        return
        [
            .. bricks.Where(b => filter switch
                {
                    BrickFilter.Installed => b.IsInstalled,
                    BrickFilter.Available => !b.IsInstalled,
                    BrickFilter.Updates => b.HasUpdate,
                    BrickFilter.BuiltIn => b.IsBuiltin,
                    _ => true,
                })
                .Where(b => string.IsNullOrWhiteSpace(search)
                            || new[] { b.Id, b.DisplayName, b.Author }.Any(text =>
                                text?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true)),
        ];
    }

    // A registry brick is declared by version range; the registry itself is how the client fetches it again.
    static string? InstallSourceOf(string? origin, PackageManifest manifest) =>
        origin is not null && origin.StartsWith("registry:", StringComparison.Ordinal) ? $"^{manifest.Version}" : origin;

    static void Offer(Dictionary<string, CatalogBrick> bricks, PackageManifest manifest, string? origin,
        string? installSource, bool stored)
    {
        if (manifest.Version is not { } version) return;

        if (!bricks.TryGetValue(manifest.Name, out var known))
        {
            bricks[manifest.Name] = new CatalogBrick(manifest.Name, manifest.DisplayName, manifest.Description,
                manifest.Author, stored ? BrickState.Installed : BrickState.Available, stored ? version.ToString() : null,
                version.ToString(), origin, installSource, IsBuiltin: !stored);
            return;
        }

        if (!Newer(known, version)) return;

        // A brick in use keeps the source it came from, and a built-in brick wins over the same id elsewhere.
        bricks[manifest.Name] = known.State == BrickState.Enabled || known.IsBuiltin
            ? known with { LatestVersion = version.ToString() }
            : known with
            {
                LatestVersion = version.ToString(),
                InstalledVersion = stored ? version.ToString() : known.InstalledVersion,
                Origin = origin,
                InstallSource = installSource,
            };
    }

    static bool Newer(CatalogBrick known, SemanticVersion version) =>
        known.LatestVersion is not { } latest || !SemanticVersion.TryParse(latest, out var have) || version > have;

    static IEnumerable<PackageManifest> BuiltinBricks(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) yield break;

        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            PackageManifest manifest;
            try
            {
                manifest = PackageManifest.Load(folder, ["gaya", ProjectPackages.HostName]);
            }
            catch (PackageException)
            {
                continue;
            }

            yield return manifest;
        }
    }
}
