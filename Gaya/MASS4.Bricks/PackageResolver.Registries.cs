namespace MASS4.Bricks;

/// <summary>Installs a package that only version ranges ask for, from a registry, checked against its signature.</summary>
public sealed partial class PackageResolver
{
    async Task<ResolvedPackage> MaterializeFromRegistryAsync(RegistryClient client, string id,
        IReadOnlyList<(string Id, VersionRange Range, string RequiredBy, int Depth)> asked, LockFile? existingLock,
        RegistryIndex? knownIndex, CancellationToken cancellationToken)
    {
        var registry = client.Registry;
        var ranges = asked.Select(static a => a.Range).ToList();
        var depth = asked.Min(static a => a.Depth);
        var locked = LockedFromRegistry(id, registry, ranges, existingLock);
        if (locked is not null && Directory.Exists(store.PackagePath(id, store.ArchiveFolderName(locked.Integrity!))))
        {
            return FromRegistry(id, store.PackagePath(id, store.ArchiveFolderName(locked.Integrity!)), locked.Integrity!,
                locked.SignedBy, registry, depth);
        }

        var index = knownIndex ?? await client.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var (version, entry, signer) = Choose(index, id, registry, ranges, asked[0].RequiredBy, locked);
        return await DownloadAsync(client, id, version, entry, signer, depth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The lock entry a registry brick stays at: one from this registry, with a version every range accepts, unless
    /// the brick is being updated; null when it is resolved anew.
    /// </summary>
    LockEntry? LockedFromRegistry(string id, ScopedRegistry registry, List<VersionRange> ranges, LockFile? existingLock) =>
        existingLock?.Dependencies.GetValueOrDefault(id) is { Version: { } version, Integrity: not null } locked
        && locked.Source == $"registry:{registry.Name}" && ranges.All(range => range.IsSatisfiedBy(version)) && KeepsLocked(id)
            ? locked
            : null;

    /// <summary>
    /// The version to download: the locked one, else the newest every range accepts; checked against the keys the
    /// registry is trusted with and, when locked, against the key that signed it before.
    /// </summary>
    (string Version, RegistryVersion Entry, string? Signer) Choose(RegistryIndex index, string id, ScopedRegistry registry,
        List<VersionRange> ranges, string requiredBy, LockEntry? locked)
    {
        if (!index.Bricks.TryGetValue(id, out var brick))
        {
            throw new PackageException($"{requiredBy} requires {id}, which {registry.Name} does not serve.");
        }

        var (version, entry) = locked is not null ? PickLocked(brick, id, locked.Version!) : PickNewest(brick, id, ranges, requiredBy);
        var signer = RegistryTrust.Verify(registry, id, version, entry);
        return locked?.SignedBy is not { } pinned || signer == pinned
            ? (version, entry, signer)
            : throw new PackageException($"{id} {version} is now signed by a different key than {LockFile.FileName} records.");
    }

    async Task<ResolvedPackage> DownloadAsync(RegistryClient client, string id, string version, RegistryVersion entry,
        string? signer, int depth, CancellationToken cancellationToken)
    {
        var file = await client.DownloadAsync(id, version, entry, Path.Combine(store.Root, ".downloads"), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var (folder, integrity) = store.ExtractArchive(file, options.ReservedCategoryPrefixes);
            store.RecordOrigin(folder, $"registry:{client.Registry.Name}");
            return FromRegistry(id, folder, integrity, signer, client.Registry, depth);
        }
        finally
        {
            File.Delete(file);
        }
    }

    ResolvedPackage FromRegistry(string id, string folder, string integrity, string? signer, ScopedRegistry registry, int depth)
    {
        var manifest = LoadChecked(id, folder, name => $"{registry.Name} served '{name}' as {id}.");
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Registry, $"registry:{registry.Name}", null, integrity,
            depth, false, signer);
    }

    static (string Version, RegistryVersion Entry) PickLocked(RegistryBrick brick, string id, SemanticVersion version) =>
        brick.Versions.FirstOrDefault(v => SemanticVersion.TryParse(v.Key, out var parsed) && parsed == version) is { Key: not null } found
            ? (found.Key, found.Value)
            : throw new PackageException($"{id} {version} is no longer in the registry.");

    (string Version, RegistryVersion Entry) PickNewest(RegistryBrick brick, string id, List<VersionRange> ranges, string requiredBy)
    {
        var candidates = brick.Versions
            .Where(v => !v.Value.Yanked && SemanticVersion.TryParse(v.Key, out _))
            .Select(v => (Text: v.Key, Version: SemanticVersion.Parse(v.Key), Entry: v.Value))
            .Where(c => ranges.All(range => range.IsSatisfiedBy(c.Version)) && RunsOnThisHost(c.Entry))
            .ToList();

        // A release beats a prerelease, which is taken only when nothing else fits.
        var best = candidates.Where(static c => !c.Version.IsPrerelease).OrderByDescending(static c => c.Version).Cast<(string, SemanticVersion, RegistryVersion)?>().FirstOrDefault()
                   ?? candidates.OrderByDescending(static c => c.Version).Cast<(string, SemanticVersion, RegistryVersion)?>().FirstOrDefault();
        return best is { } chosen
            ? (chosen.Item1, chosen.Item3)
            : throw new PackageException($"{requiredBy} requires {id} {string.Join(" and ", ranges)}, but the registry has no matching version that runs here (it has {string.Join(", ", brick.Versions.Keys)}).");
    }

    bool RunsOnThisHost(RegistryVersion entry)
    {
        var known = entry.Engines.Where(e => options.Hosts.ContainsKey(e.Key)).ToList();
        return entry.Engines.Count == 0 || (known.Count > 0 && known.All(e => e.Value.IsSatisfiedBy(options.Hosts[e.Key])));
    }
}
