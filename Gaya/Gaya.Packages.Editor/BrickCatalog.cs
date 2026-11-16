namespace Gaya.Packages.Editor;

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

    /// <summary>Bricks the project or studio uses.</summary>
    Installed,

    /// <summary>Bricks not in use, whether downloaded to this machine or not.</summary>
    Available,

    /// <summary>Bricks in use with a newer version available.</summary>
    Updates,

    /// <summary>Bricks that ship with the host.</summary>
    BuiltIn,
}

/// <summary>Which kind of content a brick provides, from its <c>categories</c>.</summary>
public enum BrickCategoryFilter
{
    /// <summary>Every brick.</summary>
    All,

    /// <summary>Looks (<c>gaya:look</c>).</summary>
    Looks,

    /// <summary>Color themes (<c>gaya:theme</c>).</summary>
    Themes,

    /// <summary>Icon themes (<c>gaya:icon-theme</c>).</summary>
    IconThemes,

    /// <summary>Font packs (<c>gaya:font-pack</c>).</summary>
    Fonts,
}

/// <summary>One copy of a brick in the shared store.</summary>
/// <param name="Folder">The store folder.</param>
/// <param name="Id">The id its manifest declares; a twin published under its own id keeps it here.</param>
/// <param name="Version">The version.</param>
/// <param name="Variant">Its form: <c>source</c>, or the bake target of a twin.</param>
public sealed record StoredCopy(string Folder, string Id, string Version, string Variant);

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
/// <param name="IsBuiltin">Whether the brick ships with the host.</param>
public sealed record CatalogBrick(string Id, string? DisplayName, string? Description, string? Author, BrickState State,
    string? InstalledVersion, string? LatestVersion, string? Origin, string? InstallSource, bool IsBuiltin)
{
    /// <summary>The complete manifest: the one in use, else the newest this client has or a store presents; null when none is known.</summary>
    public PackageManifest? Manifest { get; init; }

    /// <summary>Every version the registries list, newest first; empty when no registry lists the brick.</summary>
    public IReadOnlyList<ListedVersion> Versions { get; init; } = [];

    /// <summary>The copies in the shared store, its bake twins among them.</summary>
    public IReadOnlyList<StoredCopy> Downloads { get; init; } = [];

    /// <summary>The form in use: <c>source</c>, or the bake target of a twin; null when the brick is not in use.</summary>
    public string? UsedVariant { get; init; }

    /// <summary>
    /// The local folder the workspace links the brick to, as its link declares it; null when the brick comes from its
    /// declared source. A linked brick is whatever that folder holds, so no registry version applies to it.
    /// </summary>
    public string? Link { get; init; }

    /// <summary>Whether the project or studio uses the brick; a download in the shared store alone is not an installation.</summary>
    public bool IsInstalled => State == BrickState.Enabled;

    /// <summary>Whether the brick is in use from its declared source and the newest known version is newer than the one used.</summary>
    public bool HasUpdate => IsInstalled && Link is null && InstalledVersion is { } installed && LatestVersion is { } latest
                             && SemanticVersion.TryParse(installed, out var have) && SemanticVersion.TryParse(latest, out var newest)
                             && newest > have;

    /// <summary>Whether a workspace of <paramref name="scope"/> can use the brick; true while its scopes are unknown.</summary>
    /// <param name="scope">The workspace's scope.</param>
    /// <returns>Whether the brick installs there.</returns>
    public bool Accepts(PackageScope scope) => Manifest is null || Manifest.EffectiveScopes.Contains(scope);
}

/// <summary>
/// Merges what a project installs with what its machine and registries offer into one list: available, installed and
/// enabled bricks, one row per brick id. Bake twins published under an id of their own join their source's row.
/// Sources are read on the client; each brick's origin is the one this client used, not one the brick claims.
/// </summary>
public static class BrickCatalog
{
    /// <summary>
    /// The catalog from sources that need no network: the workspace, the host's built-in bricks and the shared store.
    /// Bricks of other scopes are listed too, so they can be found and installed where they belong.
    /// </summary>
    /// <param name="installed">What the workspace resolves: every brick it uses.</param>
    /// <param name="builtinDirectory">The folder of the bricks that ship with the host.</param>
    /// <param name="store">The shared store, or null.</param>
    /// <param name="reserved">Category prefixes only hosts may use, passed when reading manifests.</param>
    /// <returns>One entry per brick id, by name.</returns>
    public static IReadOnlyList<CatalogBrick> Local(IReadOnlyList<ResolvedPackage> installed, string? builtinDirectory,
        PackageStore? store, IReadOnlyCollection<string> reserved)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(reserved);

        var bricks = new Dictionary<string, CatalogBrick>(StringComparer.Ordinal);
        foreach (var package in installed)
        {
            bricks[package.Id] = new CatalogBrick(package.Id, package.Manifest.DisplayName, package.Manifest.Description,
                package.Manifest.Author, BrickState.Enabled,
                package.Version.ToString(), package.Version.ToString(), package.Source, package.Source,
                package.Origin == PackageOrigin.Builtin)
            {
                Manifest = package.Manifest,
                UsedVariant = VariantOf(package.Id, package.Manifest),
                Link = package.Origin == PackageOrigin.Link ? package.Source.Replace("link:", "", StringComparison.Ordinal) : null,
            };
        }

        foreach (var manifest in BuiltinBricks(builtinDirectory, reserved))
        {
            Offer(bricks, manifest, $"builtin:{manifest.Name}", null, copy: null);
        }

        foreach (var (folder, manifest) in store?.Packages(reserved) ?? [])
        {
            if (manifest.Version is { } version)
            {
                var origin = store!.ReadOrigin(folder);
                var copy = new StoredCopy(folder, manifest.Name, version.ToString(), VariantOf(SourceId(manifest), manifest));
                Offer(bricks, manifest, origin, InstallSourceOf(origin, version), copy);
            }
        }

        return Sorted(bricks.Values);
    }

    /// <summary>
    /// Adds what registries offer: every listed version, and a store's name, description and scopes. Bricks not seen
    /// yet become available, newer versions mark updates, and a brick not in use takes the registry's presentation.
    /// </summary>
    /// <param name="bricks">The catalog so far.</param>
    /// <param name="listings">What the registries list.</param>
    /// <param name="reserved">Category prefixes only hosts may use, passed when reading a store's manifests.</param>
    /// <returns>The merged catalog.</returns>
    public static IReadOnlyList<CatalogBrick> WithRegistries(IReadOnlyList<CatalogBrick> bricks,
        IEnumerable<RegistryListing> listings, IReadOnlyCollection<string>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        ArgumentNullException.ThrowIfNull(listings);

        var merged = bricks.ToDictionary(static b => b.Id, StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            if (!PackageId.IsValid(listing.Id) || listing.Latest is not { } latest || !SemanticVersion.TryParse(latest, out var version))
            {
                continue;
            }

            var manifest = Presentation(listing, reserved);
            merged[listing.Id] = merged.TryGetValue(listing.Id, out var known)
                ? Presented(Offered(known, latest, listing.Registry, version), manifest) with { Versions = listing.Versions }
                : new CatalogBrick(listing.Id, manifest?.DisplayName, manifest?.Description, manifest?.Author,
                    BrickState.Available, null, latest, $"registry:{listing.Registry}", $"^{version}", IsBuiltin: false)
                {
                    Manifest = manifest,
                    Versions = listing.Versions,
                };
        }

        return Sorted(merged.Values);
    }

    /// <summary>Narrows a catalog to a filter, a content category and a search text.</summary>
    /// <param name="bricks">The catalog.</param>
    /// <param name="filter">Which group to keep.</param>
    /// <param name="search">Text the id, name, author, description or a keyword must contain; everything when empty.</param>
    /// <param name="category">Which kind of content to keep; entries without a known manifest match only <see cref="BrickCategoryFilter.All"/>.</param>
    /// <returns>The matching bricks, in order.</returns>
    public static IReadOnlyList<CatalogBrick> Filter(IEnumerable<CatalogBrick> bricks, BrickFilter filter, string? search,
        BrickCategoryFilter category = BrickCategoryFilter.All)
    {
        ArgumentNullException.ThrowIfNull(bricks);

        var text = search?.Trim() ?? "";
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
                .Where(b => category == BrickCategoryFilter.All
                            || b.Manifest?.Categories.Contains(CategoryName(category), StringComparer.Ordinal) == true)
                .Where(b => text.Length == 0
                            || new[] { b.Id, b.DisplayName, b.Author, b.Description }.Concat(b.Manifest?.Keywords ?? [])
                                .Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)),
        ];
    }

    /// <summary>The manifest category a filter keeps.</summary>
    /// <param name="category">The filter.</param>
    /// <returns>The category, such as <c>gaya:theme</c>; empty for <see cref="BrickCategoryFilter.All"/>.</returns>
    public static string CategoryName(BrickCategoryFilter category) => category switch
    {
        BrickCategoryFilter.Looks => "gaya:look",
        BrickCategoryFilter.Themes => "gaya:theme",
        BrickCategoryFilter.IconThemes => "gaya:icon-theme",
        BrickCategoryFilter.Fonts => "gaya:font-pack",
        _ => "",
    };

    /// <summary>The id of the brick a manifest belongs with: its own, or the source's for a twin published under its own id.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The brick id the catalog lists it under.</returns>
    public static string SourceId(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.BakeOf is { Brick.Length: > 0 } bakeOf && PackageId.IsValid(bakeOf.Brick) ? bakeOf.Brick : manifest.Name;
    }

    /// <summary>A brick's form: <c>source</c>, else its bake target, else <c>bake</c> for a twin that names none.</summary>
    static string VariantOf(string id, PackageManifest manifest) =>
        manifest.Variant is { Length: > 0 } variant ? variant
        : manifest.BakeOf is not null || manifest.Name != id ? "bake" : PackageVariant.Source;

    static IReadOnlyList<CatalogBrick> Sorted(IEnumerable<CatalogBrick> bricks) =>
        [.. bricks.OrderBy(static b => b.DisplayName ?? b.Id, StringComparer.OrdinalIgnoreCase)];

    // A registry brick is declared by version range; the registry itself is how the client fetches it again.
    static string? InstallSourceOf(string? origin, SemanticVersion version) =>
        origin is not null && origin.StartsWith("registry:", StringComparison.Ordinal) ? $"^{version}" : origin;

    // A brick on the machine whose origin nobody recorded is fetched again from the registry that lists it.
    static CatalogBrick Offered(CatalogBrick known, string latest, string registry, SemanticVersion version)
    {
        var withOrigin = known.InstallSource is null && !known.IsBuiltin && known.State != BrickState.Enabled
            ? known with { Origin = $"registry:{registry}", InstallSource = $"^{version}" }
            : known;
        return Newer(withOrigin, version) ? withOrigin with { LatestVersion = latest } : withOrigin;
    }

    // The registry's presentation is the current one: it names a brick the machine holds only an old copy of.
    static CatalogBrick Presented(CatalogBrick known, PackageManifest? manifest) => manifest is null ? known : known with
    {
        DisplayName = manifest.DisplayName ?? known.DisplayName,
        Description = manifest.Description ?? known.Description,
        Author = manifest.Author ?? known.Author,
        Manifest = known.State == BrickState.Enabled && known.Manifest is not null ? known.Manifest : manifest,
    };

    static PackageManifest? Presentation(RegistryListing listing, IReadOnlyCollection<string>? reserved)
    {
        if (listing.ManifestJson is not { } json)
        {
            return null;
        }

        try
        {
            var manifest = PackageManifest.Parse(json, $"{listing.Registry}:{listing.Id}", reserved);
            return manifest.Name == listing.Id ? manifest : null;
        }
        catch (PackageException)
        {
            return null;
        }
    }

    static void Offer(Dictionary<string, CatalogBrick> bricks, PackageManifest manifest, string? origin,
        string? installSource, StoredCopy? copy)
    {
        if (manifest.Version is not { } version)
        {
            return;
        }

        var id = SourceId(manifest);
        var stored = copy is not null;
        if (!bricks.TryGetValue(id, out var known))
        {
            bricks[id] = new CatalogBrick(id, manifest.DisplayName, manifest.Description,
                manifest.Author, stored ? BrickState.Installed : BrickState.Available, stored ? version.ToString() : null,
                version.ToString(), origin, installSource, IsBuiltin: !stored)
            { Manifest = manifest, Downloads = copy is null ? [] : [copy] };
            return;
        }

        if (copy is not null)
        {
            known = known with { Downloads = [.. known.Downloads, copy] };
        }

        // A brick in use keeps the source it came from, and a built-in brick wins over the same id elsewhere.
        bricks[id] = !Newer(known, version) ? known
            : known.State == BrickState.Enabled || known.IsBuiltin ? known with { LatestVersion = version.ToString() }
            : known with
            {
                LatestVersion = version.ToString(),
                InstalledVersion = stored ? version.ToString() : known.InstalledVersion,
                Origin = origin,
                InstallSource = installSource,
                Manifest = manifest,
            };
    }

    static bool Newer(CatalogBrick known, SemanticVersion version) =>
        known.LatestVersion is not { } latest || !SemanticVersion.TryParse(latest, out var have) || version > have;

    static IEnumerable<PackageManifest> BuiltinBricks(string? directory, IReadOnlyCollection<string> reserved)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            PackageManifest manifest;
            try
            {
                manifest = PackageManifest.Load(folder, reserved);
            }
            catch (PackageException)
            {
                continue;
            }

            yield return manifest;
        }
    }
}
