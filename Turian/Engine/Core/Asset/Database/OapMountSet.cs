namespace Turian.Engine.Core;

/// <summary>
/// An ordered set of mounted Open Asset Packages, highest priority first. An asset is
/// resolved by walking the mounts in order and taking the first hit, so an overlay
/// package (DLC, patch, mod) placed ahead of the base package overrides it while
/// everything absent from the overlay falls through.
/// </summary>
public sealed class OapMountSet
{
    static readonly ConcurrentDictionary<string, WeakReference<OapMountSet>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    readonly string basePackagePath;
    readonly string fingerprint;

    OapMountSet(IReadOnlyList<OapReader> readers, string basePackagePath, string fingerprint)
    {
        Readers = readers;
        this.basePackagePath = basePackagePath;
        this.fingerprint = fingerprint;
    }

    /// <summary>Gets the mounted readers, highest priority first.</summary>
    public IReadOnlyList<OapReader> Readers { get; }

    /// <summary>
    /// Gets or resolves the mount set for a base package, layering any overlay packages
    /// found in a sibling <c>overlays</c> directory. Dependents win over their requirements;
    /// unrelated overlays are sorted by name, with later names winning.
    /// The result is cached and rebuilt when any package file changes on disk.
    /// </summary>
    /// <param name="basepackagePath">The absolute path to the base <c>.oap</c> file.</param>
    /// <returns>The mount set, or <see langword="null"/> when the base package is missing.</returns>
    public static OapMountSet? ForBasePackage(string basepackagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basepackagePath);

        var fullPath = Path.GetFullPath(basepackagePath);
        if (!File.Exists(fullPath))
        {
            Cache.TryRemove(fullPath, out _);
            return null;
        }

        var packagePaths = DiscoverPackages(fullPath);
        var fingerprint = BuildFingerprint(packagePaths);

        if (Cache.TryGetValue(fullPath, out var reference) &&
            reference.TryGetTarget(out var cached) && cached.fingerprint == fingerprint)
        {
            return cached;
        }

        var readers = OpenReaders(packagePaths);
        readers = OrderByRequirements(readers);
        WarnAboutConflicts(readers);
        var mountSet = new OapMountSet(readers, fullPath, fingerprint);
        if (Cache.Count > 64) Cache.Clear();
        Cache[fullPath] = new WeakReference<OapMountSet>(mountSet);
        return mountSet;
    }

    static List<OapReader> OpenReaders(IReadOnlyCollection<string> packagePaths)
    {
        var key = OapRuntimeKey.Current;
        var readers = new List<OapReader>(packagePaths.Count);
        foreach (var path in packagePaths)
        {
            var reader = OapReader.OpenFile(path);
            if (key is not null && (reader.Header.Flags & OapFlags.Encrypted) != 0)
            {
                reader.SetKey(key);
            }

            readers.Add(reader);
        }
        return readers;
    }

    /// <summary>Rejects package changes while a runtime session uses this mount.</summary>
    public void EnsureUnchanged()
    {
        if (!File.Exists(basePackagePath) ||
            BuildFingerprint(DiscoverPackages(basePackagePath)) != fingerprint)
            throw new InvalidDataException(
                $"OAP content changed during the active session at '{basePackagePath}'; start a new content session.");
    }

    /// <summary>Resolves an asset by its id across the mounted packages.</summary>
    /// <param name="id">The asset id.</param>
    /// <param name="reader">The package the asset was found in.</param>
    /// <param name="entry">The resolved index entry.</param>
    /// <returns><see langword="true"/> when a package holds the asset.</returns>
    public bool TryResolveById(Guid id, out OapReader reader, out OapIndexEntry entry)
    {
        foreach (var candidate in Readers)
        {
            if (candidate.FindById(id) is { } found)
            {
                reader = candidate;
                entry = found;
                return true;
            }
        }

        reader = null!;
        entry = default;
        return false;
    }

    /// <summary>Resolves an asset by its virtual path across the mounted packages.</summary>
    /// <param name="virtualPath">The virtual path.</param>
    /// <param name="reader">The package the asset was found in.</param>
    /// <param name="entry">The resolved index entry.</param>
    /// <returns><see langword="true"/> when a package holds the asset.</returns>
    public bool TryResolveByPath(string virtualPath, out OapReader reader, out OapIndexEntry entry)
    {
        foreach (var candidate in Readers)
        {
            if (candidate.FindByPath(virtualPath) is { } found)
            {
                reader = candidate;
                entry = found;
                return true;
            }
        }

        reader = null!;
        entry = default;
        return false;
    }

    static List<string> DiscoverPackages(string basePackagePath)
    {
        var packages = new List<string> { basePackagePath };

        var overlayDir = Path.Combine(Path.GetDirectoryName(basePackagePath) ?? ".", "overlays");
        if (Directory.Exists(overlayDir))
        {
            var overlays = Directory
                .EnumerateFiles(overlayDir, "*" + OapFormat.FileExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

            // Later-named overlays win, so they must appear first (highest priority).
            packages.InsertRange(0, overlays.Reverse());
        }

        return packages;
    }

    static string BuildFingerprint(IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            builder.Append(path).Append('|')
                .Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }

        return builder.ToString();
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
                if (owners.TryAdd(entry.AssetId, i) || owners[entry.AssetId] == i) continue;
                if (conflicts++ == 0) firstConflict = entry.AssetId;
            }
        }

        if (conflicts > 0)
            Log.Logger.LogWarning(
                "{Count} OAP asset override(s) occur across mounted packages (first: {AssetId}); highest-priority mount wins",
                conflicts, firstConflict);
    }

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

/// <summary>
/// The conventional fields of an OAP manifest (opaque UTF-8 JSON in the container).
/// Unknown fields are ignored and preserved verbatim in the package.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The package version string.</param>
/// <param name="Generator">The tool that produced the package.</param>
/// <param name="Requires">Names of other packages that must be mounted alongside this one.</param>
public sealed record OapManifest(string Name, string Version, string Generator, IReadOnlyList<string> Requires)
{
    /// <summary>Parses a manifest blob, returning <see langword="null"/> when it is absent or not JSON.</summary>
    /// <param name="bytes">The manifest bytes, or <see langword="null"/>.</param>
    /// <returns>The parsed manifest, or <see langword="null"/>.</returns>
    public static OapManifest? TryParse(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is not { } blob || blob.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(blob);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var requires = ParseRequires(root);
            if (requires is null) return null;

            return new OapManifest(
                GetString(root, "name"),
                GetString(root, "version"),
                GetString(root, "generator"),
                requires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static List<string>? ParseRequires(JsonElement root)
    {
        var requires = new List<string>();
        if (!root.TryGetProperty("requires", out var values)) return requires;
        if (values.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { } value || string.IsNullOrWhiteSpace(value))
                return null;
            requires.Add(value);
        }
        return requires;
    }

    static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
