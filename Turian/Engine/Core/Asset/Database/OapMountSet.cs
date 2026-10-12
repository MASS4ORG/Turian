namespace Turian.Engine.Core;

/// <summary>
/// An ordered set of mounted Open Asset Packages, highest priority first. An asset is
/// resolved by walking the mounts in order and taking the first hit, so an overlay
/// package (DLC, patch, mod) placed ahead of the base package overrides it while
/// everything absent from the overlay falls through. The set owns its packages' sources, so its
/// readers keep the bytes they mounted even when a newer build replaces a package.
/// </summary>
public sealed class OapMountSet : IDisposable
{
    const string OverlayFolder = "overlays";

    static readonly ConcurrentDictionary<string, WeakReference<OapMountSet>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    readonly IContentDirectory directory;
    readonly IReadOnlyList<string> packages;
    readonly IReadOnlyList<ContentIdentity> identities;

    OapMountSet(IReadOnlyList<OapReader> readers, IContentDirectory directory, IReadOnlyList<string> packages,
        IReadOnlyList<ContentIdentity> identities)
    {
        Readers = readers;
        this.directory = directory;
        this.packages = packages;
        this.identities = identities;
    }

    /// <summary>Gets the mounted readers, highest priority first.</summary>
    public IReadOnlyList<OapReader> Readers { get; }

    /// <summary>
    /// Gets or resolves the mount set for a base package file, layering any overlay packages
    /// found in a sibling <c>overlays</c> directory.
    /// </summary>
    /// <param name="basepackagePath">The path to the base <c>.oap</c> file.</param>
    /// <returns>The mount set, or <see langword="null"/> when the base package is missing.</returns>
    public static OapMountSet? ForBasePackage(string basepackagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basepackagePath);

        var fullPath = Path.GetFullPath(basepackagePath);
        var directory = new FileContentDirectory(Path.GetDirectoryName(fullPath) ?? fullPath);
        return ForBasePackage(directory, Path.GetFileName(fullPath));
    }

    /// <summary>
    /// Gets or resolves the mount set for a base package in a content location, layering any overlay
    /// packages in its <c>overlays</c> folder. Dependents win over their requirements; unrelated overlays
    /// are sorted by name, with later names winning. The result is cached and rebuilt when the identity of
    /// any package changes.
    /// </summary>
    /// <param name="directory">The location holding the base package and its <c>overlays</c> folder.</param>
    /// <param name="basePackage">The base package path relative to <paramref name="directory"/>.</param>
    /// <returns>The mount set, or <see langword="null"/> when the base package is missing.</returns>
    public static OapMountSet? ForBasePackage(IContentDirectory directory, string basePackage)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePackage);

        var cacheKey = directory.Id + "|" + basePackage;
        if (directory.Probe(basePackage) is null)
        {
            Cache.TryRemove(cacheKey, out _);
            return null;
        }

        var packages = DiscoverPackages(directory, basePackage);
        if (Cache.TryGetValue(cacheKey, out var reference) &&
            reference.TryGetTarget(out var cached) && cached.IsCurrent(directory, packages))
        {
            return cached;
        }

        var (readers, identities) = OpenReaders(directory, packages);
        try
        {
            var ordered = OrderByRequirements(readers);
            WarnAboutConflicts(ordered);
            var mountSet = new OapMountSet(ordered, directory, packages, identities);
            if (Cache.Count > 64) Cache.Clear();
            Cache[cacheKey] = new WeakReference<OapMountSet>(mountSet);
            return mountSet;
        }
        catch
        {
            foreach (var reader in readers) reader.Dispose();
            throw;
        }
    }

    /// <summary>Rejects package changes while a runtime session uses this mount, so one session never mixes
    /// content from two builds.</summary>
    public void EnsureUnchanged()
    {
        if (!IsCurrent(directory, DiscoverPackages(directory, packages[^1])))
            throw new InvalidDataException(
                $"OAP content changed during the active session at '{identities[^1].Id}'; start a new content session.");
    }

    /// <summary>Releases the mounted packages' sources.</summary>
    public void Dispose()
    {
        foreach (var reader in Readers) reader.Dispose();
    }

    // Compares probed identities with the mounted ones, so detecting a change never opens or reads a package.
    bool IsCurrent(IContentDirectory location, IReadOnlyList<string> current)
    {
        if (current.Count != identities.Count) return false;
        for (var i = 0; i < current.Count; i++)
            if (!string.Equals(current[i], packages[i], StringComparison.Ordinal) ||
                location.Probe(current[i]) != identities[i])
                return false;
        return true;
    }

    static (List<OapReader> Readers, ContentIdentity[] Identities) OpenReaders(IContentDirectory directory,
        IReadOnlyList<string> packages)
    {
        var key = OapRuntimeKey.Current;
        var readers = new List<OapReader>(packages.Count);
        var identities = new ContentIdentity[packages.Count];
        try
        {
            for (var i = 0; i < packages.Count; i++)
            {
                var source = directory.Open(packages[i]);
                identities[i] = source.Identity;
                var reader = OpenReader(source);
                if (key is not null && (reader.Header.Flags & OapFlags.Encrypted) != 0)
                {
                    reader.SetKey(key);
                }

                readers.Add(reader);
            }
        }
        catch
        {
            foreach (var reader in readers) reader.Dispose();
            throw;
        }

        return (readers, identities);
    }

    static OapReader OpenReader(IContentSource source)
    {
        try
        {
            return OapReader.Open(source, ownsSource: true);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves an asset by its id across the mounted packages. The highest package holding the id decides: a
    /// tombstone there removes the asset even when a lower package still has it.
    /// </summary>
    /// <param name="id">The asset id.</param>
    /// <param name="reader">The package the asset was found in.</param>
    /// <param name="entry">The resolved index entry.</param>
    /// <returns><see langword="true"/> when a package holds the asset.</returns>
    public bool TryResolveById(Guid id, out OapReader reader, out OapIndexEntry entry) =>
        TryResolve(candidate => candidate.FindById(id), out reader, out entry);

    /// <summary>Resolves an asset by its virtual path across the mounted packages, honoring tombstones.</summary>
    /// <param name="virtualPath">The virtual path.</param>
    /// <param name="reader">The package the asset was found in.</param>
    /// <param name="entry">The resolved index entry.</param>
    /// <returns><see langword="true"/> when a package holds the asset.</returns>
    public bool TryResolveByPath(string virtualPath, out OapReader reader, out OapIndexEntry entry) =>
        TryResolve(candidate => candidate.FindByPath(virtualPath), out reader, out entry);

    bool TryResolve(Func<OapReader, OapIndexEntry?> find, out OapReader reader, out OapIndexEntry entry)
    {
        foreach (var candidate in Readers)
        {
            if (find(candidate) is not { } found) continue;
            if (found.IsTombstone) break;

            reader = candidate;
            entry = found;
            return true;
        }

        reader = null!;
        entry = default;
        return false;
    }

    static List<string> DiscoverPackages(IContentDirectory directory, string basePackage)
    {
        var folder = basePackage.LastIndexOf('/') is var slash and >= 0 ? basePackage[..slash] + "/" : "";

        // Later-named overlays win, so they must appear first (highest priority).
        var packages = directory.List(folder + OverlayFolder, OapFormat.FileExtension).Reverse().ToList();
        packages.Add(basePackage);
        return packages;
    }

    static List<OapReader> OrderByRequirements(IReadOnlyList<OapReader> readers)
    {
        var manifests = ReadManifests(readers);
        var dependencies = BuildDependencies(manifests, IndexNames(manifests));
        return SortReaders(readers, dependencies);
    }

    static Dictionary<string, int> IndexNames(IReadOnlyList<OapManifest?> manifests)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < manifests.Count; i++)
            if (manifests[i] is { Name.Length: > 0 } manifest && !byName.TryAdd(manifest.Name, i))
                throw new InvalidOperationException($"Multiple OAP packages are named '{manifest.Name}'.");
        return byName;
    }

    static List<OapReader> SortReaders(IReadOnlyList<OapReader> readers, IReadOnlyList<int[]> dependencies)
    {
        var dependents = CountDependents(dependencies);
        var ready = ReadyPackages(dependents);
        var ordered = new List<OapReader>(readers.Count);
        while (ready.TryDequeue(out var index, out _))
        {
            ordered.Add(readers[index]);
            foreach (var dependency in dependencies[index])
                if (--dependents[dependency] == 0) ready.Enqueue(dependency, dependency);
        }

        if (ordered.Count != readers.Count)
            throw new InvalidOperationException("OAP package requirements form a cycle.");
        return ordered;
    }

    static int[] CountDependents(IReadOnlyList<int[]> dependencies)
    {
        var dependents = new int[dependencies.Count];
        foreach (var packageDependencies in dependencies)
            foreach (var dependency in packageDependencies)
                dependents[dependency]++;
        return dependents;
    }

    static PriorityQueue<int, int> ReadyPackages(IReadOnlyList<int> dependents)
    {
        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < dependents.Count; i++)
            if (dependents[i] == 0) ready.Enqueue(i, i);
        return ready;
    }

    static OapManifest?[] ReadManifests(IReadOnlyList<OapReader> readers)
    {
        var manifests = new OapManifest?[readers.Count];
        for (var i = 0; i < readers.Count; i++)
        {
            manifests[i] = OapManifest.TryParse(readers[i].Manifest);
            if (readers[i].Manifest is { Length: > 0 } && manifests[i] is null)
                throw new InvalidOperationException($"OAP mount {i} has an invalid manifest.");
        }
        return manifests;
    }

    static int[][] BuildDependencies(IReadOnlyList<OapManifest?> manifests, IReadOnlyDictionary<string, int> byName)
    {
        var dependencies = new int[manifests.Count][];
        for (var i = 0; i < manifests.Count; i++)
        {
            var manifest = manifests[i];
            var resolved = new List<int>();
            if (manifest is not null)
                foreach (var required in manifest.Requires)
                {
                    if (!byName.TryGetValue(required, out var dependency))
                        throw new InvalidOperationException(
                            $"OAP package '{manifest.Name}' requires '{required}', which is not mounted.");
                    if (i == manifests.Count - 1)
                        throw new InvalidOperationException(
                            $"Base OAP package '{manifest.Name}' cannot require overlay '{required}'.");
                    resolved.Add(dependency);
                }
            dependencies[i] = [.. resolved.Distinct()];
        }
        return dependencies;
    }

    static void WarnAboutConflicts(IReadOnlyList<OapReader> readers)
    {
        if (readers.Count < 2) return;

        var owners = new Dictionary<Guid, int>();
        var conflicts = 0;
        Guid firstConflict = default;
        for (var i = 0; i < readers.Count; i++)
        {
            foreach (var entry in readers[i].Entries)
            {
                if (!IsOverride(owners, entry, i)) continue;
                if (conflicts++ == 0) firstConflict = entry.AssetId;
            }
        }

        if (conflicts > 0)
            Log.Logger.LogWarning(
                "{Count} OAP asset override(s) occur across mounted packages (first: {AssetId}); highest-priority mount wins",
                conflicts, firstConflict);
    }

    // An asset another mount already holds; tombstones remove rather than override, so they never count.
    static bool IsOverride(Dictionary<Guid, int> owners, OapIndexEntry entry, int mount) =>
        !entry.IsTombstone && !owners.TryAdd(entry.AssetId, mount) && owners[entry.AssetId] != mount;

}

/// <summary>
/// Holds the process-wide key used to decrypt encrypted Open Asset Packages at
/// runtime. By default the key is derived from the <c>TURIAN_OAP_KEY</c> environment
/// variable when set; a host can override it with <see cref="Set"/>.
/// </summary>
public static class OapRuntimeKey
{
    static byte[]? _overrideKey;
    static bool _overrideSet;

    /// <summary>Gets the active 32-byte key, or <see langword="null"/> when none is configured.</summary>
    public static byte[]? Current
    {
        get
        {
            if (_overrideSet)
            {
                return _overrideKey;
            }

            var passphrase = Environment.GetEnvironmentVariable("TURIAN_OAP_KEY");
            return string.IsNullOrEmpty(passphrase) ? null : OapCrypto.DeriveKey(passphrase);
        }
    }

    /// <summary>Overrides the runtime key. Pass <see langword="null"/> to disable decryption.</summary>
    /// <param name="key">A 32-byte key, or <see langword="null"/>.</param>
    public static void Set(byte[]? key)
    {
        if (key is { Length: not OapCrypto.KeyLength })
        {
            throw new ArgumentException($"An OAP key must be {OapCrypto.KeyLength} bytes.", nameof(key));
        }

        _overrideKey = key;
        _overrideSet = true;
    }

    /// <summary>Derives and sets the runtime key from a passphrase.</summary>
    /// <param name="passphrase">The passphrase.</param>
    public static void SetPassphrase(string passphrase) => Set(OapCrypto.DeriveKey(passphrase));
}
