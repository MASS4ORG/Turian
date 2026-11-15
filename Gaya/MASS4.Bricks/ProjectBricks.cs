namespace MASS4.Bricks;

/// <summary>Edits of a project's brick declarations, shared by every host's command line and panel.</summary>
public static class ProjectBricks
{
    /// <summary>Declares a brick in the project's <c>Bricks/manifest.json</c>, replacing an earlier declaration of the id.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="spec">The source or version range, as <c>manifest.json</c> writes it.</param>
    /// <exception cref="PackageException">The id or the value is invalid.</exception>
    public static void Add(string projectRoot, string id, string spec)
    {
        if (!PackageId.IsValid(id))
        {
            throw new PackageException($"'{id}' is not a valid package id.");
        }

        _ = PackageSource.Parse(spec, Path.Combine(projectRoot, ProjectManifest.DirectoryName));

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        manifest.Dependencies[id] = spec;
        manifest.Save(projectRoot);
    }

    /// <summary>Removes a brick's declaration and moves its embedded source, if any, to the project's trash.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the project declared or embedded it.</returns>
    public static bool Remove(string projectRoot, string id)
    {
        if (!PackageId.IsValid(id))
        {
            throw new PackageException($"'{id}' is not a valid package id.");
        }

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        var embedded = Path.Combine(projectRoot, ProjectManifest.DirectoryName, id);
        var hasEmbedded = File.Exists(Path.Combine(embedded, PackageManifest.FileName));
        var declared = manifest.Dependencies.Remove(id) | manifest.Links.Remove(id);
        if (!declared && !hasEmbedded)
        {
            return false;
        }

        string? trash = null;
        if (hasEmbedded)
        {
            trash = Path.Combine(projectRoot, ".Cache", "Trash", Guid.NewGuid().ToString("N"), id);
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(embedded, trash);
        }

        try
        {
            if (declared)
            {
                manifest.Save(projectRoot);
            }
        }
        catch
        {
            if (trash is not null)
            {
                Directory.Move(trash, embedded);
            }

            throw;
        }
        return true;
    }

    /// <summary>
    /// Copies an installed brick into the project's <c>Bricks/&lt;id&gt;</c> folder as a writable fork, which wins
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
        {
            throw new PackageException($"{package.Id} is licensed so that it cannot be embedded; use it from its registry, or ask its publisher.");
        }

        var target = Path.Combine(projectRoot, ProjectManifest.DirectoryName, package.Id);
        if (package.Origin == PackageOrigin.Embedded || Directory.Exists(target))
        {
            throw new PackageException($"{package.Id} is already embedded at {target}.");
        }

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
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            throw;
        }
    }

    /// <summary>
    /// Moves a brick's local fork to the project's trash, so the brick is used from its declared source again.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <returns>The folder the fork moved to.</returns>
    /// <exception cref="PackageException">The project holds no fork of the brick, or the fork has no global original.</exception>
    public static string Revert(string projectRoot, string id,
        IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        if (!PackageId.IsValid(id))
        {
            throw new PackageException($"'{id}' is not a valid package id.");
        }

        var fork = Path.Combine(projectRoot, ProjectManifest.DirectoryName, id);
        if (!File.Exists(Path.Combine(fork, PackageManifest.FileName)))
        {
            throw new PackageException($"{id} is not local to the project.");
        }

        if (PackageManifest.Load(fork, reservedCategoryPrefixes).Upstream is null)
        {
            throw new PackageException($"{id} was made in this project and has no global version to go back to.");
        }

        var trash = Path.Combine(projectRoot, ".Cache", "Trash", Guid.NewGuid().ToString("N"), id);
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        Directory.Move(fork, trash);
        return trash;
    }

    /// <summary>
    /// Stops linking a brick to a local folder, in this machine's override files and in the shared manifest, so the
    /// brick comes from its declared source again. The linked folder is left as it is.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether any file linked it.</returns>
    /// <exception cref="PackageException">A file is not a valid manifest.</exception>
    public static bool Unlink(string projectRoot, string id)
    {
        var directory = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var personal = RemoveLink(Path.Combine(directory, ProjectManifest.UserFileName), id)
                       | RemoveLink(Path.Combine(directory, ProjectManifest.LinksFileName), id);
        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        if (!manifest.Links.Remove(id))
        {
            return personal;
        }

        manifest.Save(projectRoot);
        return true;
    }

    // Removes one entry of a file's "links" object, keeping the rest of the file as written.
    static bool RemoveLink(string path, string id)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
                ?? throw new PackageException($"{path} is not a valid project manifest.");
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid project manifest: {ex.Message}", ex);
        }

        if (node["links"] is not JsonObject links || !links.Remove(id))
        {
            return false;
        }

        if (links.Count == 0)
        {
            _ = node.Remove("links");
        }

        File.WriteAllText(path, node.ToJsonString(PackageJson.Options));
        return true;
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .Where(static d => !IsVersionControl(d)))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

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
