namespace MASS4.Bricks;

/// <summary>
/// Takes a registry brick's bake twin in its place: the twin of the version the ranges resolve to, signed like any
/// brick, published by the source's own publisher or a baker the source trusts, and baked from the exact source file
/// the registry serves.
/// </summary>
public sealed partial class PackageResolver
{
    readonly Dictionary<string, IReadOnlyList<BuildAdvisory>> advisoriesByRegistry = new(StringComparer.Ordinal);

    /// <summary>
    /// The trusted twin of the version of <paramref name="id"/> the ranges resolve to; null when none is published or
    /// the one published is refused, with the reason added to <paramref name="warnings"/>.
    /// </summary>
    async Task<ResolvedPackage?> MaterializeTwinAsync(RegistryClient client, string id,
        IReadOnlyList<(string Id, VersionRange Range, string RequiredBy, int Depth)> asked, LockFile? existingLock,
        RegistryIndex? knownIndex, List<string> warnings, CancellationToken cancellationToken)
    {
        var registry = client.Registry;
        var ranges = asked.Select(static a => a.Range).ToList();
        var depth = asked.Min(static a => a.Depth);
        var lockedTwin = LockedTwin(id, registry, ranges, existingLock);
        if (lockedTwin is { Id: var lockedId, Entry.Integrity: { } lockedIntegrity }
            && Directory.Exists(store.PackagePath(lockedId, store.ArchiveFolderName(lockedIntegrity))))
        {
            return FromRegistry(lockedId, store.PackagePath(lockedId, store.ArchiveFolderName(lockedIntegrity)),
                lockedIntegrity, lockedTwin.Value.Entry.SignedBy, registry, depth);
        }

        // Installing locked follows the lock, which pins the source here; an index that cannot be read leaves the
        // source path to use what the store holds or report why it cannot.
        if (lockedTwin is null && options.Locked
            || (knownIndex ?? await TryGetIndexAsync(client, cancellationToken).ConfigureAwait(false)) is not { } index)
        {
            return null;
        }

        var sourceLock = LockedFromRegistry(id, registry, ranges, existingLock)
                         ?? (lockedTwin?.Entry.BakeOf is { Version: { } bakedVersion } ? new LockEntry { Version = bakedVersion } : null);
        var (version, source, _) = Choose(index, id, registry, ranges, asked[0].RequiredBy, sourceLock);
        return TwinEntry(index, id, version, source, lockedTwin?.Entry.Variant, locked: lockedTwin is not null) is { } found
            ? await TakeTwinAsync(client, found, (id, version, source), lockedTwin?.Entry.SignedBy, depth, warnings,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Verifies, downloads and checks a bake found for a source version; null, with the reason added to
    /// <paramref name="warnings"/>, when the source must be used instead.
    /// </summary>
    /// <exception cref="PackageException">The bake is now signed by another key than the lock pins.</exception>
    async Task<ResolvedPackage?> TakeTwinAsync(RegistryClient client, (string Id, string Version, RegistryVersion Entry) found,
        (string Id, string Version, RegistryVersion Entry) source, string? pinned, int depth, List<string> warnings,
        CancellationToken cancellationToken)
    {
        string? signer;
        try
        {
            signer = RegistryTrust.Verify(client.Registry, found.Id, found.Version, found.Entry);
        }
        catch (PackageException ex)
        {
            warnings.Add($"{ex.Message} Using {source.Id} {source.Version} as source.");
            return null;
        }

        if (pinned is not null && signer != pinned)
        {
            throw new PackageException($"{found.Id} {found.Version} is now signed by a different key than {LockFile.FileName} records.");
        }

        var twin = await DownloadAsync(client, found.Id, found.Version, found.Entry, signer, depth, cancellationToken).ConfigureAwait(false);
        var problem = await TwinProblemAsync(client, twin, found.Entry, source.Id, source.Version, source.Entry, cancellationToken).ConfigureAwait(false)
                      ?? Unloadable(twin, found.Entry)
                      ?? await AdvisoryProblemAsync(client, twin, found.Entry, warnings, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            warnings.Add($"{problem} Using {source.Id} {source.Version} as source.");
            return null;
        }

        return twin;
    }

    /// <summary>
    /// Why this host cannot load a bake, which is then left for its source: precast assemblies built by another
    /// major or minor version of a host, or a bake recorded as built by another major of the build host.
    /// </summary>
    string? Unloadable(ResolvedPackage twin, RegistryVersion entry) => PrecastProblem(twin) ?? BuildMajorProblem(twin, entry);

    string? PrecastProblem(ResolvedPackage twin)
    {
        foreach (var (host, running) in options.Hosts)
        {
            if (twin.Manifest.Precast?.BuiltWith.TryGetValue(host, out var built) == true
                && (built.Major != running.Major || built.Minor != running.Minor))
            {
                return $"{twin.Id} {twin.Version} ({PackageVariant.Of(twin.Manifest, twin.Integrity!)}) was baked with {host} {built}, which {host} {running} does not load.";
            }
        }

        return null;
    }

    string? BuildMajorProblem(ResolvedPackage twin, RegistryVersion entry) =>
        options.BuildHost is { } buildHost && options.Hosts.TryGetValue(buildHost, out var current)
        && SemanticVersion.TryParse(entry.Build?.TurianVersion, out var builder) && builder.Major != current.Major
            ? $"{twin.Id} {twin.Version} was built by {buildHost} {builder}, another major version than {current}."
            : null;

    /// <summary>The build host version a bake was built with: the store's record, else its precast's.</summary>
    string? BuildVersionOf(ResolvedPackage twin, RegistryVersion entry) => entry.Build?.TurianVersion
        ?? (options.BuildHost is { } buildHost ? twin.Manifest.Precast?.BuiltWith.GetValueOrDefault(buildHost)?.ToString() : null);

    /// <summary>
    /// Why a curator advisory of the registry's store refuses a bake, matched on the build the store recorded and,
    /// when it recorded none, on the version the bake's precast records; warning advisories are added to
    /// <paramref name="warnings"/>.
    /// </summary>
    async Task<string?> AdvisoryProblemAsync(RegistryClient client, ResolvedPackage twin, RegistryVersion entry,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var advisories = await AdvisoriesAsync(client.Registry, cancellationToken).ConfigureAwait(false);
        if (advisories.Count == 0)
        {
            return null;
        }

        var (rejected, warning) = BuildAdvisory.Evaluate(advisories, twin.Id, [(entry.Build?.Image, BuildVersionOf(twin, entry))]);
        var origin = StoreClient.OriginOf(client.Registry.Url);
        warnings.AddRange(warning.Select(a => $"{twin.Id} {twin.Version}: curator advisory {a.Id}: {a.Message} See {new Uri(origin, a.Path)}."));
        return rejected is null
            ? null
            : $"{twin.Id} {twin.Version} was built by a toolchain curator advisory {rejected.Id} rejects: {rejected.Message} See {new Uri(origin, rejected.Path)}.";
    }

    /// <summary>The build advisories of the store behind an http registry, read once; none for other registries or when unreadable.</summary>
    async Task<IReadOnlyList<BuildAdvisory>> AdvisoriesAsync(ScopedRegistry registry, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(registry.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return [];
        }

        if (!advisoriesByRegistry.TryGetValue(registry.Url, out var advisories))
        {
            try
            {
                advisories = await new StoreClient(registry.Url, RegistryClient.TokenFor(registry), options.Http)
                    .BuildAdvisoriesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PackageException)
            {
                advisories = [];
            }

            advisoriesByRegistry[registry.Url] = advisories;
        }

        return advisories;
    }

    static async Task<RegistryIndex?> TryGetIndexAsync(RegistryClient client, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PackageException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks a twin the project asked for by its own id against the source the same registry serves.
    /// </summary>
    /// <exception cref="PackageException">The twin is not a trusted bake of that source.</exception>
    async Task CheckRequestedTwinAsync(RegistryClient client, ResolvedPackage twin, RegistryIndex index,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var bakeOf = twin.Manifest.BakeOf!;
        var version = bakeOf.Version!.ToString();
        if (index.Bricks.GetValueOrDefault(bakeOf.Brick)?.Versions.GetValueOrDefault(version) is not { } source)
        {
            throw new PackageException($"{twin.Id} {twin.Version} is a bake of {bakeOf.Brick} {version}, which {client.Registry.Name} does not serve; its source cannot be verified.");
        }

        var entry = index.Bricks[twin.Id].Versions[twin.Version.ToString()];
        if (await TwinProblemAsync(client, twin, entry, bakeOf.Brick, version, source, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new PackageException(problem);
        }
    }

    /// <summary>Why a downloaded twin cannot stand in for the source version; null when it can.</summary>
    async Task<string?> TwinProblemAsync(RegistryClient client, ResolvedPackage twin, RegistryVersion twinEntry,
        string id, string version, RegistryVersion source, CancellationToken cancellationToken)
    {
        if (twin.Manifest.BakeOf is not { } bakeOf || bakeOf.Brick != id || bakeOf.Version != SemanticVersion.Parse(version))
        {
            return $"{twin.Id} {twin.Version} is not a bake of {id} {version}.";
        }

        if (bakeOf.Integrity != source.Integrity)
        {
            return $"{twin.Id} {twin.Version} was baked from another {id} {version} file than {client.Registry.Name} serves.";
        }

        _ = RegistryTrust.Verify(client.Registry, id, version, source);
        if (SamePublisher(source, twinEntry))
        {
            return null;
        }

        // Only the source's own manifest says which other bakers it trusts.
        var signer = RegistryTrust.Verify(client.Registry, id, version, source);
        var original = await DownloadAsync(client, id, version, source, signer, twin.Depth, cancellationToken).ConfigureAwait(false);
        if (!original.Manifest.AllowsTwins)
        {
            return $"{id} {version} is published as its own consumable form and refuses bake twins.";
        }

        return twinEntry.PublisherKey is { Length: > 0 } baker && original.Manifest.TrustedBakers.Contains(SshSignature.Fingerprint(baker))
            ? null
            : $"{twin.Id} {twin.Version} was published by neither {id}'s publisher nor a baker it trusts.";
    }

    /// <summary>
    /// Whether one publisher released both entries: the same publisher key, or no publisher key on either, where the
    /// registry alone vouches for both.
    /// </summary>
    static bool SamePublisher(RegistryVersion source, RegistryVersion twin) =>
        (source.PublisherKey, twin.PublisherKey) switch
        {
            (null or "", null or "") => true,
            ({ Length: > 0 } a, { Length: > 0 } b) => SshSignature.Fingerprint(a) == SshSignature.Fingerprint(b),
            _ => false,
        };

    /// <summary>
    /// The bake of a source version: the variant the lock records, else the variant this host prefers, else the twin
    /// the index links from before variants, else <c>id.bake</c> at the same version. A yanked bake counts only for a
    /// project that locked it.
    /// </summary>
    (string Id, string Version, RegistryVersion Entry)? TwinEntry(RegistryIndex index, string id, string version,
        RegistryVersion source, string? lockedVariant, bool locked)
    {
        return PreferredVariants(source, lockedVariant).Select(key => source.Variants![key])
                .FirstOrDefault(variant => locked || !variant.Yanked) is { } chosen
            ? (chosen.PackageId ?? id, chosen.Version ?? version, chosen)
            : LegacyTwinEntry(index, id, version, source, locked);
    }

    /// <summary>A bake from before variants: the one the index links, else <c>id.bake</c> at the same version.</summary>
    static (string Id, string Version, RegistryVersion Entry)? LegacyTwinEntry(RegistryIndex index, string id, string version,
        RegistryVersion source, bool locked)
    {
        IEnumerable<RegistryBakeLink> candidates = [.. source.Bakes ?? [], new RegistryBakeLink { Brick = BrickArchive.TwinIdOf(id), Version = version }];
        foreach (var link in candidates)
        {
            if (index.Bricks.GetValueOrDefault(link.Brick)?.Versions.GetValueOrDefault(link.Version) is { } entry && (locked || !entry.Yanked))
            {
                return (link.Brick, link.Version, entry);
            }
        }

        return null;
    }

    /// <summary>
    /// The bake variants of a source version this host can take, best first: the locked one, then each host's own
    /// major and minor, then that major's older minors, newest first. Another major is never taken; its source is.
    /// </summary>
    IEnumerable<string> PreferredVariants(RegistryVersion source, string? lockedVariant)
    {
        if (source.Variants is not { Count: > 0 } variants)
        {
            return [];
        }

        IEnumerable<string> locked = lockedVariant is not null && variants.ContainsKey(lockedVariant) ? [lockedVariant] : [];
        return locked.Concat(options.Hosts.OrderBy(static h => h.Key, StringComparer.Ordinal)
                .SelectMany(host => ForHost(variants.Keys, host.Key, host.Value)))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>The keys a host of <paramref name="running"/> takes: its own minor, then its major's older ones, newest first.</summary>
    static IEnumerable<string> ForHost(IEnumerable<string> keys, string host, SemanticVersion running) => keys
        .Select(key => (Key: key, Minor: MinorOf(key, host, running.Major)))
        .Where(k => k.Minor <= running.Minor)
        .OrderByDescending(static k => k.Minor)
        .Select(static k => k.Key);

    /// <summary>The minor version a key such as <c>turian3.3</c> names for this host and major; null for other keys.</summary>
    static int? MinorOf(string key, string host, int major)
    {
        var prefix = $"{host}{major}.";
        return key.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? minor
            : null;
    }

    /// <summary>
    /// The lock entry of a twin standing in for <paramref name="id"/> from this registry, at a source version every
    /// range accepts, unless the brick is being updated; null when there is none.
    /// </summary>
    (string Id, LockEntry Entry)? LockedTwin(string id, ScopedRegistry registry, List<VersionRange> ranges, LockFile? existingLock) =>
        existingLock?.Dependencies.FirstOrDefault(d => d.Value.BakeOf?.Brick == id) is { Key: not null } locked
        && locked.Value is { Integrity: not null, BakeOf.Version: { } version }
        && locked.Value.Source == $"registry:{registry.Name}" && ranges.All(range => range.IsSatisfiedBy(version)) && KeepsLocked(id)
            ? (locked.Key, locked.Value)
            : null;
}
