namespace MASS4.Bricks;

/// <summary>One published version of a brick as a registry lists it.</summary>
/// <param name="Version">The version.</param>
/// <param name="Yanked">Withdrawn: kept for projects that locked it, never chosen for a new resolution.</param>
public sealed record ListedVersion(string Version, bool Yanked)
{
    /// <summary>The archive forms the registry publishes of the version, such as <c>source</c> and <c>turian4.1</c>.</summary>
    public IReadOnlyList<string> Variants { get; init; } = [];

    /// <summary>The host versions the version works with, as its manifest declares them.</summary>
    public IReadOnlyDictionary<string, VersionRange> Engines { get; init; } = new Dictionary<string, VersionRange>();

    /// <summary>The archive's size in bytes, when the registry is a store that says.</summary>
    public long? Size { get; init; }

    /// <summary>Why consumers should move off the version, when a store deprecated it.</summary>
    public string? Deprecation { get; init; }
}

/// <summary>What a registry offers of one brick: every version, and how a store presents its newest one.</summary>
/// <param name="Registry">The registry's name.</param>
/// <param name="Id">The brick id.</param>
/// <param name="Versions">Every listed version, newest first.</param>
public sealed record RegistryListing(string Registry, string Id, IReadOnlyList<ListedVersion> Versions)
{
    /// <summary>
    /// The newest unwithdrawn version's <c>package.json</c> text as a store serves it, for its name, description and
    /// scopes; null for a plain registry. Unsigned, so it informs and never authorizes.
    /// </summary>
    public string? ManifestJson { get; init; }

    /// <summary>The newest version that is not withdrawn, or null when every version is.</summary>
    public string? Latest => Versions.FirstOrDefault(static v => !v.Yanked)?.Version;
}

/// <summary>Everything a workspace's registries list, and what could not be read.</summary>
/// <param name="Bricks">One listing per registry and brick.</param>
/// <param name="Problems">Why a registry could not be read, one message per registry.</param>
public sealed record RegistryCatalog(IReadOnlyList<RegistryListing> Bricks, IReadOnlyList<string> Problems);

public static partial class BrickRegistries
{
    /// <summary>
    /// Lists every brick the registries serve with all its versions. A registry that is also a store adds each
    /// brick's presentation and archive sizes from its public catalog; a plain registry lists versions only.
    /// </summary>
    /// <param name="registries">The registries to read.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The listings, and a message for each registry that could not be read.</returns>
    public static async Task<RegistryCatalog> ListAsync(IEnumerable<ScopedRegistry> registries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var bricks = new List<RegistryListing>();
        var problems = new List<string>();
        foreach (var registry in registries)
        {
            RegistryIndex index;
            try
            {
                index = await new RegistryClient(registry).GetIndexAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PackageException ex)
            {
                problems.Add($"Registry {registry.Name}: {ex.Message}");
                continue;
            }

            var served = index.Bricks.Where(b => registry.Serves(b.Key)).ToList();
            var details = await StoreListings.DescribeAsync(registry, [.. served.Select(static b => b.Key)], cancellationToken)
                .ConfigureAwait(false);
            bricks.AddRange(served.Select(b => Listing(registry.Name, b.Key, b.Value, details.GetValueOrDefault(b.Key))));
        }

        return new RegistryCatalog(bricks, problems);
    }

    static RegistryListing Listing(string registry, string id, RegistryBrick brick, StoreListings.Detail? detail)
    {
        var versions = brick.Versions
            .Where(static v => SemanticVersion.TryParse(v.Key, out _))
            .OrderByDescending(static v => SemanticVersion.Parse(v.Key))
            .Select(v => new ListedVersion(v.Key, v.Value.Yanked)
            {
                Variants = v.Value.Variants is { Count: > 0 } variants ? [.. variants.Keys] : [v.Value.Variant ?? PackageVariant.Source],
                Engines = v.Value.Engines,
                Size = detail?.Versions.GetValueOrDefault(v.Key)?.Size,
                Deprecation = detail?.Versions.GetValueOrDefault(v.Key)?.Deprecation,
            })
            .ToList();
        var newest = versions.FirstOrDefault(static v => !v.Yanked)?.Version;
        return new RegistryListing(registry, id, versions)
        {
            ManifestJson = newest is null ? null : detail?.Versions.GetValueOrDefault(newest)?.ManifestJson,
        };
    }
}
