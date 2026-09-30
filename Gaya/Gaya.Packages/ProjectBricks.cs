namespace Gaya.Packages;

/// <summary>Edits of a project's brick declarations, shared by every host's command line and panel.</summary>
public static class ProjectBricks
{
    /// <summary>Declares a brick in the project's <c>Packages/manifest.json</c>, replacing an earlier declaration of the id.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="spec">The source or version range, as <c>manifest.json</c> writes it.</param>
    /// <exception cref="PackageException">The id or the value is invalid.</exception>
    public static void Add(string projectRoot, string id, string spec)
    {
        if (!PackageId.IsValid(id)) throw new PackageException($"'{id}' is not a valid package id.");
        _ = PackageSource.Parse(spec, Path.Combine(projectRoot, ProjectManifest.DirectoryName));

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        manifest.Dependencies[id] = spec;
        manifest.Save(projectRoot);
    }

    /// <summary>Removes a brick's declaration from the project's manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the manifest declared it.</returns>
    public static bool Remove(string projectRoot, string id)
    {
        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        if (!manifest.Dependencies.Remove(id)) return false;

        manifest.Save(projectRoot);
        return true;
    }

    /// <summary>
    /// Copies an installed brick into the project's <c>Packages/&lt;id&gt;</c> folder as a writable fork, which wins
    /// over the declared source from then on, and records where it came from in its <c>package.json</c>.
    /// </summary>
    /// <param name="package">The installed brick.</param>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <returns>The fork's folder.</returns>
    /// <exception cref="PackageException">The brick is already embedded.</exception>
    public static string Embed(ResolvedPackage package, string projectRoot,
        IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Manifest.Store is { Embeddable: false })
            throw new PackageException($"{package.Id} is licensed so that it cannot be embedded; use it from its registry, or ask its publisher.");

        var target = Path.Combine(projectRoot, ProjectManifest.DirectoryName, package.Id);
        if (package.Origin == PackageOrigin.Embedded || Directory.Exists(target))
            throw new PackageException($"{package.Id} is already embedded at {target}.");

        try
        {
            CopyDirectory(package.RootPath, target);
            var manifest = PackageManifest.Load(target, reservedCategoryPrefixes);
            var pin = package.Commit ?? package.Integrity;
            manifest.Upstream = pin is null ? $"{package.Id}@{package.Version}" : $"{package.Id}@{package.Version} ({pin})";
            manifest.Save(target);
            return target;
        }
        catch
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            throw;
        }
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .Where(static d => !IsVersionControl(d)))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(static f => !IsVersionControl(f)))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, destination);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    static bool IsVersionControl(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".git");
}
