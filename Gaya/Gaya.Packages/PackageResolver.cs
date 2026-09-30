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
}

/// <summary>One package of a resolved project.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Manifest">Its <c>package.json</c>.</param>
/// <param name="RootPath">The folder the package is read from.</param>
/// <param name="Origin">How it reached the project.</param>
/// <param name="Source">The dependency value it came from, as written.</param>
/// <param name="Commit">The git commit, for a git source.</param>
/// <param name="Integrity">The store folder's content hash, for a git source.</param>
/// <param name="Depth">1 when the project installs it itself, deeper when only another package needs it.</param>
/// <param name="IsOverridden">Whether the per-user manifest override chose its source.</param>
public sealed record ResolvedPackage(
    string Id,
    PackageManifest Manifest,
    string RootPath,
    PackageOrigin Origin,
    string Source,
    string? Commit,
    string? Integrity,
    int Depth,
    bool IsOverridden)
{
    /// <summary>The resolved version.</summary>
    public SemanticVersion Version => Manifest.Version!;

    /// <summary>Whether the package must not be edited: store and built-in folders are shared by every project.</summary>
    public bool IsReadOnly => Origin is PackageOrigin.Git or PackageOrigin.Builtin;
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

/// <summary>Choices that change how a project's packages resolve.</summary>
public sealed record PackageResolverOptions
{
    /// <summary>Host → its version, checked against each package's <see cref="PackageManifest.Engines"/>.</summary>
    public IReadOnlyDictionary<string, SemanticVersion> Hosts { get; init; } = new Dictionary<string, SemanticVersion>();

    /// <summary>What the packages are installed into; a package must list it among its scopes.</summary>
    public PackageScope Scope { get; init; } = PackageScope.Project;

    /// <summary>The folder holding the host's built-in packages, one per id; null when the host ships none.</summary>
    public string? BuiltinDirectory { get; init; }

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
        var constraints = new List<(string Id, VersionRange Range, string RequiredBy)>();
        var queue = new Queue<(string Id, PackageSource Source, string Spec, string RequiredBy, int Depth)>();

        foreach (var embedded in EmbeddedPackages(packagesDirectory, options.ReservedCategoryPrefixes))
            queue.Enqueue((embedded.Id, new FileSource(embedded.Path), "embedded", "the project", 1));

        foreach (var (id, spec) in manifest.Dependencies.Where(static d => d.Value is not null)
                     .OrderBy(static d => d.Key, StringComparer.Ordinal))
            queue.Enqueue((id, PackageSource.Parse(spec!, packagesDirectory), spec!, "the project", 1));

        while (queue.TryDequeue(out var next))
        {
            if (next.Source is RangeSource range)
            {
                constraints.Add((next.Id, range.Range, next.RequiredBy));
                continue;
            }

            if (resolved.ContainsKey(next.Id)) continue;

            var package = await MaterializeAsync(next.Id, next.Source, next.Spec, next.Depth,
                overridden.Contains(next.Id), existingLock, cancellationToken).ConfigureAwait(false);
            resolved[next.Id] = package;

            foreach (var (id, spec) in package.Manifest.Dependencies.OrderBy(static d => d.Key, StringComparer.Ordinal))
                queue.Enqueue((id, PackageSource.Parse(spec, package.RootPath), spec, package.Id, next.Depth + 1));
        }

        foreach (var (id, range, requiredBy) in constraints)
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
                Depth = package.Depth,
                Dependencies = new SortedDictionary<string, string>(package.Manifest.Dependencies, StringComparer.Ordinal),
            };
        }

        return lockFile;
    }
}
