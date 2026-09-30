namespace Gaya.Packages;

/// <summary>How a resolved package reached the project.</summary>
public enum PackageOrigin
{
    /// <summary>A folder committed under the project's <c>Packages</c> folder; wins over any declaration.</summary>
    Embedded,

    /// <summary>A local folder, used in place.</summary>
    File,

    /// <summary>A git repository, extracted into the store.</summary>
    Git,

    /// <summary>Shipped with the host and versioned with it.</summary>
    Builtin,

    /// <summary>A packed <c>.brick</c> file, extracted into the store.</summary>
    Archive,

    /// <summary>Downloaded from a registry, checked against its signature, and extracted into the store.</summary>
    Registry,
}

/// <summary>One package of a resolved project.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Manifest">Its <c>package.json</c>.</param>
/// <param name="RootPath">The folder the package is read from.</param>
/// <param name="Origin">How it reached the project.</param>
/// <param name="Source">The dependency value it came from, as written.</param>
/// <param name="Commit">The git commit, for a git source.</param>
/// <param name="Integrity">The content hash: of the store folder for a git source, of the file for a <c>.brick</c>.</param>
/// <param name="Depth">1 when the project installs it itself, deeper when only another package needs it.</param>
/// <param name="IsOverridden">Whether the per-user manifest override chose its source.</param>
/// <param name="SignedBy">The fingerprint of the key that signed a registry package; null otherwise.</param>
public sealed record ResolvedPackage(
    string Id,
    PackageManifest Manifest,
    string RootPath,
    PackageOrigin Origin,
    string Source,
    string? Commit,
    string? Integrity,
    int Depth,
    bool IsOverridden,
    string? SignedBy = null)
{
    /// <summary>The resolved version.</summary>
    public SemanticVersion Version => Manifest.Version!;

    /// <summary>Whether the package must not be edited: store and built-in folders are shared by every project.</summary>
    public bool IsReadOnly => Origin is PackageOrigin.Git or PackageOrigin.Builtin or PackageOrigin.Archive or PackageOrigin.Registry;
}

/// <summary>The packages a project installs, each after the packages it depends on, and the lock that pins them.</summary>
/// <param name="Packages">The packages, dependencies first.</param>
/// <param name="Lock">The lock file describing them.</param>
public sealed record PackageResolution(IReadOnlyList<ResolvedPackage> Packages, LockFile Lock)
{
    /// <summary>
    /// Whether the per-user manifest override chose any package. Such a resolution is local to one machine, so its
    /// lock must not replace the committed one.
    /// </summary>
    public bool UsesUserOverride => Packages.Any(static p => p.IsOverridden);
}

/// <summary>A package's folder fetched from one source, without resolving its dependencies.</summary>
/// <param name="Folder">The package folder.</param>
/// <param name="Commit">The git commit, for a git source.</param>
/// <param name="Integrity">The content hash, for a git source or a <c>.brick</c> file.</param>
public sealed record FetchedPackage(string Folder, string? Commit, string? Integrity);

/// <summary>Choices that change how a project's packages resolve.</summary>
public sealed record PackageResolverOptions
{
    /// <summary>Host → its version, checked against each package's <see cref="PackageManifest.Engines"/>.</summary>
    public IReadOnlyDictionary<string, SemanticVersion> Hosts { get; init; } = new Dictionary<string, SemanticVersion>();

    /// <summary>What the packages are installed into; a package must list it among its scopes.</summary>
    public PackageScope Scope { get; init; } = PackageScope.Project;

    /// <summary>The folder holding the host's built-in packages, one per id; null when the host ships none.</summary>
    public string? BuiltinDirectory { get; init; }

    /// <summary>
    /// Registries the host takes bricks from besides the ones the project declares, such as the public one. A range no
    /// declaration provides is looked up here.
    /// </summary>
    public IReadOnlyList<ScopedRegistry> Registries { get; init; } = [];

    /// <summary>The HTTP client registries are read with; a shared one when null.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>Category prefixes any package may use without depending on a package of that id.</summary>
    public IReadOnlyCollection<string> ReservedCategoryPrefixes { get; init; } = [];

    /// <summary>
    /// Install exactly what the lock file records and fail when the manifest asks for anything else; the per-user
    /// override is ignored. What CI builds use.
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>Package ids whose git refs are fetched again instead of staying at their locked commit; null for all.</summary>
    public IReadOnlySet<string>? Update { get; init; } = new HashSet<string>();
}

/// <summary>
/// Resolves a project's packages: the embedded ones, those its manifest declares, and, to any depth, the packages
/// those depend on. Each id resolves to exactly one package; the first declaration found wins (embedded, then the
/// project manifest, then dependencies breadth-first) and every version range anyone states must then hold.
/// </summary>
/// <param name="store">Where git packages are fetched into.</param>
/// <param name="options">Resolution choices.</param>
public sealed class PackageResolver(PackageStore store, PackageResolverOptions? options = null)
{
    readonly PackageResolverOptions options = options ?? new PackageResolverOptions();

    /// <summary>Resolves the packages of the project at <paramref name="projectRoot"/>.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="cancellationToken">Cancels git work.</param>
    /// <returns>The resolved packages and their lock.</returns>
    /// <exception cref="PackageException">A package is missing, invalid, incompatible or conflicting.</exception>
    public async Task<PackageResolution> ResolveAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var packagesDirectory = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var (manifest, overridden) = ProjectManifest.Load(projectRoot, includeUserOverride: !options.Locked);
        var existingLock = LockFile.Load(projectRoot);
        if (options.Locked && existingLock is null)
            throw new PackageException($"{projectRoot} has no {LockFile.FileName} to install from.");

        var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        var constraints = new List<(string Id, VersionRange Range, string RequiredBy, int Depth)>();
        var registries = new List<ScopedRegistry>([.. manifest.ScopedRegistries, .. options.Registries]);
        var queue = new Queue<(string Id, PackageSource Source, string Spec, string RequiredBy, int Depth)>();

        foreach (var embedded in EmbeddedPackages(packagesDirectory, options.ReservedCategoryPrefixes))
            queue.Enqueue((embedded.Id, new FileSource(embedded.Path), "embedded", "the project", 1));

        foreach (var (id, spec) in manifest.Dependencies.Where(static d => d.Value is not null)
                     .OrderBy(static d => d.Key, StringComparer.Ordinal))
            queue.Enqueue((id, PackageSource.Parse(spec!, packagesDirectory), spec!, "the project", 1));

        async Task DrainAsync()
        {
            while (queue.TryDequeue(out var next))
            {
                if (next.Source is RangeSource range)
                {
                    constraints.Add((next.Id, range.Range, next.RequiredBy, next.Depth));
                    continue;
                }

                if (resolved.ContainsKey(next.Id)) continue;

                var package = await MaterializeAsync(next.Id, next.Source, next.Spec, next.Depth,
                    overridden.Contains(next.Id), existingLock, cancellationToken).ConfigureAwait(false);
                AddPackage(package);
            }
        }

        void AddPackage(ResolvedPackage package)
        {
            resolved[package.Id] = package;
            foreach (var (id, spec) in package.Manifest.Dependencies.OrderBy(static d => d.Key, StringComparer.Ordinal))
                queue.Enqueue((id, PackageSource.Parse(spec, package.RootPath), spec, package.Id, package.Depth + 1));
        }

        await DrainAsync().ConfigureAwait(false);

        // What nothing declared a source for comes from the registry scoped to its name, until no new brick appears.
        var clients = new Dictionary<string, RegistryClient>(StringComparer.Ordinal);
        while (true)
        {
            var progressed = false;
            foreach (var group in constraints.Where(c => !resolved.ContainsKey(c.Id)).GroupBy(static c => c.Id).ToList())
            {
                if (registries.FirstOrDefault(r => r.Serves(group.Key)) is not { } registry) continue;

                if (!clients.TryGetValue(registry.Name, out var client)) clients[registry.Name] = client = new RegistryClient(registry, options.Http);
                AddPackage(await MaterializeFromRegistryAsync(client, group.Key, [.. group], existingLock, cancellationToken).ConfigureAwait(false));
                progressed = true;
            }

            if (!progressed) break;
            await DrainAsync().ConfigureAwait(false);
        }

        foreach (var (id, range, requiredBy, _) in constraints)
        {
            if (!resolved.TryGetValue(id, out var package))
                throw new PackageException(
                    $"{requiredBy} requires {id} {range}, which nothing provides; add it to {ProjectManifest.DirectoryName}/{ProjectManifest.FileName}.");
            if (!range.IsSatisfiedBy(package.Version))
                throw new PackageException(
                    $"{requiredBy} requires {id} {range}, but {package.Version} is installed (from {package.Source}).");
        }

        var ordered = DependencyOrder(resolved);
        var lockFile = ToLock(ordered);
        if (options.Locked && existingLock!.Serialize() != lockFile.Serialize())
            throw new PackageException(
                $"{ProjectManifest.DirectoryName}/{ProjectManifest.FileName} no longer matches {LockFile.FileName}; resolve without --locked and commit the lock file.");

        return new PackageResolution(ordered, lockFile);
    }

    async Task<ResolvedPackage> MaterializeFromRegistryAsync(RegistryClient client, string id,
        IReadOnlyList<(string Id, VersionRange Range, string RequiredBy, int Depth)> asked, LockFile? existingLock,
        CancellationToken cancellationToken)
    {
        var registry = client.Registry;
        var ranges = asked.Select(static a => a.Range).ToList();
        var depth = asked.Min(static a => a.Depth);
        var locked = existingLock?.Dependencies.GetValueOrDefault(id);
        var keepLocked = locked is { Version: not null, Integrity: not null } && locked.Source == $"registry:{registry.Name}"
                         && ranges.All(range => range.IsSatisfiedBy(locked.Version!))
                         && (options.Locked || options.Update is { } update && !update.Contains(id));

        if (keepLocked)
        {
            var stored = store.PackagePath(id, store.ArchiveFolderName(locked!.Integrity!));
            if (Directory.Exists(stored))
                return Finish(id, stored, locked.Integrity!, locked.SignedBy, registry, depth);
        }

        var index = await client.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (!index.Bricks.TryGetValue(id, out var brick))
            throw new PackageException($"{asked[0].RequiredBy} requires {id}, which {registry.Name} does not serve.");

        var (version, entry) = keepLocked
            ? PickLocked(brick, id, locked!.Version!)
            : PickNewest(brick, id, ranges, asked[0].RequiredBy);

        var signer = RegistryTrust.Verify(registry, id, version, entry);
        if (keepLocked && locked!.SignedBy is { } pinned && signer != pinned)
            throw new PackageException($"{id} {version} is now signed by a different key than {LockFile.FileName} records.");

        var downloads = Path.Combine(store.Root, ".downloads");
        var file = await client.DownloadAsync(id, version, entry, downloads, cancellationToken).ConfigureAwait(false);
        try
        {
            var (folder, integrity) = store.ExtractArchive(file, options.ReservedCategoryPrefixes);
            store.RecordOrigin(folder, $"registry:{registry.Name}");
            return Finish(id, folder, integrity, signer, registry, depth);
        }
        finally
        {
            File.Delete(file);
        }
    }

    ResolvedPackage Finish(string id, string folder, string integrity, string? signer, ScopedRegistry registry, int depth)
    {
        var manifest = PackageManifest.Load(folder, options.ReservedCategoryPrefixes);
        if (manifest.Name != id) throw new PackageException($"{registry.Name} served '{manifest.Name}' as {id}.");
        if (!manifest.EffectiveScopes.Contains(options.Scope))
            throw new PackageException($"{id} cannot be installed with scope {options.Scope}; its scopes are {string.Join(", ", manifest.EffectiveScopes)}.");
        CheckEngines(manifest);

        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Registry, $"registry:{registry.Name}", null, integrity, depth, false, signer);
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

    /// <summary>
    /// Fetches one package from <paramref name="source"/> into the store, or finds it in place, without resolving what
    /// it depends on. For comparing against a package rather than installing it.
    /// </summary>
    /// <param name="id">The package id.</param>
    /// <param name="source">Where the package comes from.</param>
    /// <param name="cancellationToken">Cancels git work.</param>
    /// <returns>The package folder and what pins it.</returns>
    /// <exception cref="PackageException">The source cannot be fetched or does not hold the package.</exception>
    public async Task<FetchedPackage> FetchAsync(string id, PackageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        FetchedPackage fetched;
        switch (source)
        {
            case FileSource file when Directory.Exists(file.Path):
                fetched = new FetchedPackage(file.Path, null, null);
                break;
            case ArchiveSource archive when File.Exists(archive.Path):
                var (folder, hash) = store.ExtractArchive(archive.Path, options.ReservedCategoryPrefixes);
                fetched = new FetchedPackage(folder, null, hash);
                break;
            case BuiltinSource when options.BuiltinDirectory is { } builtins && Directory.Exists(Path.Combine(builtins, id)):
                fetched = new FetchedPackage(Path.Combine(builtins, id), null, null);
                break;
            case GitSource git:
                var commit = await store.ResolveCommitAsync(git, cancellationToken).ConfigureAwait(false);
                var root = store.PackagePath(id, commit);
                if (!Directory.Exists(root)) root = await store.CheckoutAsync(git, commit, cancellationToken).ConfigureAwait(false);
                fetched = new FetchedPackage(root, commit, PackageStore.ComputeIntegrity(root));
                break;
            default:
                throw new PackageException($"{id}: {source} cannot be found.");
        }

        var manifest = PackageManifest.Load(fetched.Folder, options.ReservedCategoryPrefixes);
        return manifest.Name == id ? fetched : throw new PackageException($"{source} holds package '{manifest.Name}', not '{id}'.");
    }

    async Task<ResolvedPackage> MaterializeAsync(string id, PackageSource source, string spec, int depth,
        bool isOverridden, LockFile? existingLock, CancellationToken cancellationToken)
    {
        string root;
        PackageOrigin origin;
        string? commit = null, integrity = null;

        switch (source)
        {
            case FileSource file:
                if (!Directory.Exists(file.Path)) throw new PackageException($"{id}: folder {file.Path} does not exist.");
                root = file.Path;
                origin = spec == "embedded" ? PackageOrigin.Embedded : PackageOrigin.File;
                break;

            case ArchiveSource archive:
                if (!File.Exists(archive.Path)) throw new PackageException($"{id}: file {archive.Path} does not exist.");
                (root, integrity) = store.ExtractArchive(archive.Path, options.ReservedCategoryPrefixes);
                store.RecordOrigin(root, archive.ToString());
                if (options.Locked && existingLock?.Dependencies.GetValueOrDefault(id) is { Integrity: { } pinned } && pinned != integrity)
                    throw new PackageException($"{id}: {archive.Path} does not match the integrity in {LockFile.FileName}.");
                origin = PackageOrigin.Archive;
                break;

            case BuiltinSource builtin:
                if (builtin.Id != id) throw new PackageException($"{id}: {spec} names another package.");
                root = options.BuiltinDirectory is { } builtins ? Path.Combine(builtins, id) : "";
                if (!Directory.Exists(root)) throw new PackageException($"{id} is not a built-in package of this host.");
                origin = PackageOrigin.Builtin;
                break;

            case GitSource git:
                var locked = existingLock?.Dependencies.GetValueOrDefault(id);
                var keepLocked = locked is { Commit: not null } && locked.Source == spec
                                 && (options.Locked || options.Update is { } update && !update.Contains(id));
                commit = keepLocked ? locked!.Commit! : await store.ResolveCommitAsync(git, cancellationToken).ConfigureAwait(false);

                root = store.PackagePath(id, commit);
                var checkedOut = !Directory.Exists(root);
                if (checkedOut) root = await store.CheckoutAsync(git, commit, cancellationToken).ConfigureAwait(false);
                store.RecordOrigin(root, git.ToString());

                // Hashing a large package is slow, so an unchanged store folder is trusted unless installing locked.
                integrity = keepLocked && !checkedOut && !options.Locked && locked!.Integrity is { } known
                    ? known
                    : PackageStore.ComputeIntegrity(root);
                if (keepLocked && locked!.Integrity is { } expected && expected != integrity)
                    throw new PackageException($"{id}: store folder {root} does not match the integrity in {LockFile.FileName}.");
                origin = PackageOrigin.Git;
                break;

            default:
                throw new PackageException($"{id}: {source} is not a source that can be installed from.");
        }

        var manifest = PackageManifest.Load(root, options.ReservedCategoryPrefixes);
        if (manifest.Name != id)
            throw new PackageException($"{spec} holds package '{manifest.Name}', not '{id}'.");
        if (!manifest.EffectiveScopes.Contains(options.Scope))
            throw new PackageException(
                $"{id} cannot be installed with scope {options.Scope}; its scopes are {string.Join(", ", manifest.EffectiveScopes)}.");
        CheckEngines(manifest);

        return new ResolvedPackage(id, manifest, root, origin, spec, commit, integrity, depth, isOverridden);
    }

    void CheckEngines(PackageManifest manifest)
    {
        if (manifest.Engines.Count == 0) return;

        var known = manifest.Engines.Where(e => options.Hosts.ContainsKey(e.Key)).ToList();
        if (known.Count == 0)
            throw new PackageException($"{manifest.Name} targets {string.Join(", ", manifest.Engines.Keys)}, none of which is this host.");

        foreach (var (host, range) in known)
        {
            var version = options.Hosts[host];
            if (!range.IsSatisfiedBy(version))
                throw new PackageException($"{manifest.Name} {manifest.Version} requires {host} {range}; this is {version}.");
        }
    }

    static IEnumerable<(string Id, string Path)> EmbeddedPackages(string packagesDirectory,
        IReadOnlyCollection<string> reservedCategoryPrefixes)
    {
        if (!Directory.Exists(packagesDirectory)) yield break;

        foreach (var directory in Directory.EnumerateDirectories(packagesDirectory).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, PackageManifest.FileName))) continue;
            yield return (PackageManifest.Load(directory, reservedCategoryPrefixes).Name, directory);
        }
    }

    static List<ResolvedPackage> DependencyOrder(Dictionary<string, ResolvedPackage> resolved)
    {
        var ordered = new List<ResolvedPackage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(ResolvedPackage package)
        {
            if (!visited.Add(package.Id)) return;
            foreach (var id in package.Manifest.Dependencies.Keys.Order(StringComparer.Ordinal))
                if (resolved.TryGetValue(id, out var dependency)) Visit(dependency);
            ordered.Add(package);
        }

        foreach (var package in resolved.Values.OrderBy(static p => p.Id, StringComparer.Ordinal)) Visit(package);
        return ordered;
    }

    static LockFile ToLock(IEnumerable<ResolvedPackage> packages)
    {
        var lockFile = new LockFile();
        foreach (var package in packages)
        {
            lockFile.Dependencies[package.Id] = new LockEntry
            {
                Version = package.Version,
                Source = package.Source,
                Commit = package.Commit,
                Integrity = package.Integrity,
                SignedBy = package.SignedBy,
                Depth = package.Depth,
                Dependencies = new SortedDictionary<string, string>(package.Manifest.Dependencies, StringComparer.Ordinal),
            };
        }

        return lockFile;
    }
}
