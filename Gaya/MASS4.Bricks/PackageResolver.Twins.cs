namespace MASS4.Bricks;

/// <summary>
/// Takes a registry brick's bake twin in its place: the twin of the version the ranges resolve to, signed like any
/// brick, published by the source's own publisher or a baker the source trusts, and baked from the exact source file
/// the registry serves.
/// </summary>
public sealed partial class PackageResolver
{
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
        if (TwinEntry(index, id, version, source, locked: lockedTwin is not null) is not { } found)
        {
            return null;
        }

        string? signer;
        try
        {
            signer = RegistryTrust.Verify(registry, found.Id, found.Version, found.Entry);
        }
        catch (PackageException ex)
        {
            warnings.Add($"{ex.Message} Using {id} {version} as source.");
            return null;
        }

        if (lockedTwin?.Entry.SignedBy is { } pinned && signer != pinned)
        {
            throw new PackageException($"{found.Id} {found.Version} is now signed by a different key than {LockFile.FileName} records.");
        }

        var twin = await DownloadAsync(client, found.Id, found.Version, found.Entry, signer, depth, cancellationToken).ConfigureAwait(false);
        if (await TwinProblemAsync(client, twin, found.Entry, id, version, source, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            warnings.Add($"{problem} Using {id} {version} as source.");
            return null;
        }

        return twin;
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
    /// The twin of a source version: the one the index links, else <c>id.bake</c> at the same version. A yanked twin
    /// counts only for a project that locked it.
    /// </summary>
    static (string Id, string Version, RegistryVersion Entry)? TwinEntry(RegistryIndex index, string id, string version,
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
