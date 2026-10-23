namespace MASS4.Bricks;

/// <summary>Installs a package from a declared source: a folder, a <c>.brick</c> file, a built-in or a git repository.</summary>
public sealed partial class PackageResolver
{
    /// <summary>Where a declared package was found, and what pins it.</summary>
    /// <param name="Root">The package folder.</param>
    /// <param name="Origin">How it reached the project.</param>
    /// <param name="Commit">The git commit, for a git source.</param>
    /// <param name="Integrity">The content hash, for a git source or a <c>.brick</c> file.</param>
    readonly record struct Located(string Root, PackageOrigin Origin, string? Commit = null, string? Integrity = null);

    async Task<ResolvedPackage> MaterializeAsync(string id, PackageSource source, string spec, int depth,
        bool isOverridden, LockFile? existingLock, CancellationToken cancellationToken)
    {
        var located = source switch
        {
            FileSource file => FromFolder(id, file, spec),
            LinkSource link => Directory.Exists(link.Path)
                ? new Located(link.Path, PackageOrigin.Link)
                : throw new PackageException($"{id}: linked folder {link.Path} does not exist; restore the folder or remove the project link."),
            ArchiveSource archive => FromArchive(id, archive, existingLock),
            BuiltinSource builtin => FromBuiltin(id, builtin, spec),
            GitSource git => await FromGitAsync(id, git, spec, existingLock, cancellationToken).ConfigureAwait(false),
            _ => throw new PackageException($"{id}: {source} is not a source that can be installed from."),
        };

        var manifest = LoadChecked(id, located.Root, name => $"{spec} holds package '{name}', not '{id}'.");
        return new ResolvedPackage(id, manifest, located.Root, located.Origin, spec, located.Commit, located.Integrity,
            depth, isOverridden);
    }

    static Located FromFolder(string id, FileSource file, string spec) =>
        Directory.Exists(file.Path)
            ? new Located(file.Path, spec == "embedded" ? PackageOrigin.Embedded : PackageOrigin.File)
            : throw new PackageException($"{id}: folder {file.Path} does not exist.");

    Located FromArchive(string id, ArchiveSource archive, LockFile? existingLock)
    {
        if (!File.Exists(archive.Path))
        {
            throw new PackageException($"{id}: file {archive.Path} does not exist.");
        }

        var (root, integrity) = store.ExtractArchive(archive.Path, options.ReservedCategoryPrefixes);
        store.RecordOrigin(root, archive.ToString());
        if (options.Locked && existingLock?.Dependencies.GetValueOrDefault(id) is { Integrity: { } pinned } && pinned != integrity)
        {
            throw new PackageException($"{id}: {archive.Path} does not match the integrity in {LockFile.FileName}.");
        }

        return new Located(root, PackageOrigin.Archive, Integrity: integrity);
    }

    Located FromBuiltin(string id, BuiltinSource builtin, string spec)
    {
        if (builtin.Id != id)
        {
            throw new PackageException($"{id}: {spec} names another package.");
        }

        var root = options.BuiltinDirectory is { } builtins ? Path.Combine(builtins, id) : "";
        if (IsBuiltin(root))
        {
            return new Located(root, PackageOrigin.Builtin);
        }

        throw new PackageException(Directory.Exists(root)
            ? $"{id} is not a built-in package of this host: {root} has no {PackageManifest.FileName}."
            : $"{id} is not a built-in package of this host.");
    }

    async Task<Located> FromGitAsync(string id, GitSource git, string spec, LockFile? existingLock,
        CancellationToken cancellationToken)
    {
        var locked = existingLock?.Dependencies.GetValueOrDefault(id);
        var keepLocked = locked is { Commit: not null } && locked.Source == spec && KeepsLocked(id);
        var commit = keepLocked ? locked!.Commit! : await store.ResolveCommitAsync(git, cancellationToken).ConfigureAwait(false);

        var root = store.PackagePath(id, commit);
        var checkedOut = !Directory.Exists(root);
        if (checkedOut)
        {
            root = await store.CheckoutAsync(git, commit, cancellationToken).ConfigureAwait(false);
        }

        store.RecordOrigin(root, git.ToString());
        return new Located(root, PackageOrigin.Git, commit, GitIntegrity(id, root, keepLocked ? locked!.Integrity : null, checkedOut));
    }

    /// <summary>
    /// The content hash of a git package's store folder, checked against the one the lock records. Hashing a large
    /// package is slow, so an unchanged store folder is trusted unless installing locked.
    /// </summary>
    string GitIntegrity(string id, string root, string? known, bool checkedOut)
    {
        var integrity = known is not null && !checkedOut && !options.Locked ? known : PackageStore.ComputeIntegrity(root);
        return known is null || known == integrity
            ? integrity
            : throw new PackageException($"{id}: store folder {root} does not match the integrity in {LockFile.FileName}.");
    }

    /// <summary>Whether a locked package stays at what the lock records: always when installing locked, else unless updated.</summary>
    bool KeepsLocked(string id) => options.Locked || options.Update is { } update && !update.Contains(id);

    /// <summary>
    /// Whether a built-in folder holds a brick; build output left behind by another branch has no manifest.
    /// </summary>
    static bool IsBuiltin(string root) => File.Exists(Path.Combine(root, PackageManifest.FileName));
}
