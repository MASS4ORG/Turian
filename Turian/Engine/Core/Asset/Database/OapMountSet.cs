namespace Turian.Engine.Core;

/// <summary>
/// An ordered set of mounted Open Asset Packages, highest priority first. An asset is
/// resolved by walking the mounts in order and taking the first hit, so an overlay
/// package (DLC, patch, mod) placed ahead of the base package overrides it while
/// everything absent from the overlay falls through.
/// </summary>
public sealed class OapMountSet
{
    static readonly ConcurrentDictionary<string, CachedMountSet> cache = new(StringComparer.OrdinalIgnoreCase);

    OapMountSet(IReadOnlyList<OapReader> readers) => Readers = readers;

    /// <summary>Gets the mounted readers, highest priority first.</summary>
    public IReadOnlyList<OapReader> Readers { get; }

    /// <summary>
    /// Gets or resolves the mount set for a base package, layering any overlay packages
    /// found in a sibling <c>overlays</c> directory (sorted by name, later names win).
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
            cache.TryRemove(fullPath, out _);
            return null;
        }

        var packagePaths = DiscoverPackages(fullPath);
        var fingerprint = BuildFingerprint(packagePaths);

        if (cache.TryGetValue(fullPath, out var cached) && cached.Fingerprint == fingerprint)
        {
            return cached.MountSet;
        }

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

        var mountSet = new OapMountSet(readers);
        cache[fullPath] = new CachedMountSet(fingerprint, mountSet);
        WarnAboutUnmetRequirements(readers);
        return mountSet;
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

    static void WarnAboutUnmetRequirements(IReadOnlyList<OapReader> readers)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reader in readers)
        {
            if (OapManifest.TryParse(reader.Manifest) is { Name.Length: > 0 } manifest)
            {
                present.Add(manifest.Name);
            }
        }

        foreach (var reader in readers)
        {
            var manifest = OapManifest.TryParse(reader.Manifest);
            if (manifest is null)
            {
                continue;
            }

            foreach (var required in manifest.Requires)
            {
                if (!present.Contains(required))
                {
                    Log.Logger.LogWarning(
                        "OAP package '{Package}' requires '{Required}', which is not mounted",
                        manifest.Name,
                        required);
                }
            }
        }
    }

    readonly record struct CachedMountSet(string Fingerprint, OapMountSet MountSet);
}

/// <summary>
/// Holds the process-wide key used to decrypt encrypted Open Asset Packages at
/// runtime. By default the key is derived from the <c>TURIAN_OAP_KEY</c> environment
/// variable when set; a host can override it with <see cref="Set"/>.
/// </summary>
public static class OapRuntimeKey
{
    static byte[]? overrideKey;
    static bool overrideSet;

    /// <summary>Gets the active 32-byte key, or <see langword="null"/> when none is configured.</summary>
    public static byte[]? Current
    {
        get
        {
            if (overrideSet)
            {
                return overrideKey;
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

        overrideKey = key;
        overrideSet = true;
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

            var requires = new List<string>();
            if (root.TryGetProperty("requires", out var requiresElement) &&
                requiresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiresElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                    {
                        requires.Add(value);
                    }
                }
            }

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

    static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
