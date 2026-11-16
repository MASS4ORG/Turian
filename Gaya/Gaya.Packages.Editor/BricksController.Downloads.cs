namespace Gaya.Packages.Editor;

public sealed partial class BricksController
{
    /// <summary>
    /// The copies of a brick in the shared store this machine can do without: every one except the copy in use, the
    /// copies the project's and the studio's lock files pin, and the newest version. Another project that pinned a
    /// removed copy downloads it again on its next restore.
    /// </summary>
    /// <param name="brick">The brick.</param>
    /// <returns>The removable copies.</returns>
    public IReadOnlyList<StoredCopy> UnusedDownloads(CatalogBrick brick)
    {
        ArgumentNullException.ThrowIfNull(brick);
        return Removable(brick.Downloads, KeptFolders());
    }

    /// <summary>Every copy in the shared store <see cref="UnusedDownloads"/> would remove, across all bricks.</summary>
    /// <returns>The removable copies.</returns>
    public IReadOnlyList<StoredCopy> StaleDownloads()
    {
        var kept = KeptFolders();
        return [.. Catalog.SelectMany(brick => Removable(brick.Downloads, kept))];
    }

    /// <summary>Deletes a brick's <see cref="UnusedDownloads"/> from the shared store.</summary>
    /// <param name="brick">The brick.</param>
    /// <returns>Whether the copies were deleted.</returns>
    public Task<bool> RemoveUnusedDownloadsAsync(CatalogBrick brick) =>
        DeleteDownloadsAsync($"Remove old downloads of {brick.Id}", UnusedDownloads(brick));

    /// <summary>Deletes every <see cref="StaleDownloads"/> copy from the shared store.</summary>
    /// <returns>Whether the copies were deleted.</returns>
    public Task<bool> CleanUpDownloadsAsync() => DeleteDownloadsAsync("Clean up brick downloads", StaleDownloads());

    /// <summary>The size of a store copy on disk, in bytes; zero when it cannot be read.</summary>
    /// <param name="copy">The copy.</param>
    /// <returns>The bytes its files take.</returns>
    public static long SizeOf(StoredCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        try
        {
            return Directory.Exists(copy.Folder)
                ? Directory.EnumerateFiles(copy.Folder, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>A byte count as people read it, such as <c>1.4 MB</c>.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>The text.</returns>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    Task<bool> DeleteDownloadsAsync(string label, IReadOnlyList<StoredCopy> copies) =>
        copies.Count == 0
            ? Task.FromResult(true)
            : RunAsync(label, _ => Delete(copies), applyToProject: false);

    void Delete(IReadOnlyList<StoredCopy> copies)
    {
        foreach (var copy in copies)
        {
            _ = Store.Delete(copy.Folder);
        }
    }

    // The newest version stays as the one to enable next; any copy a workspace here uses or pins stays too.
    static IReadOnlyList<StoredCopy> Removable(IReadOnlyList<StoredCopy> copies, IReadOnlySet<string> kept)
    {
        var newest = copies.Select(static c => SemanticVersion.TryParse(c.Version, out var v) ? v : null).OfType<SemanticVersion>()
            .DefaultIfEmpty().Max();
        return
        [
            .. copies.Where(c => !kept.Contains(Path.GetFileName(c.Folder)) && !kept.Contains(Pin(c.Folder))
                                 && !(SemanticVersion.TryParse(c.Version, out var v) && v == newest)),
        ];
    }

    // What follows the '@' in a store folder's name: the archive hash or git commit a lock entry pins.
    static string Pin(string folder)
    {
        var name = Path.GetFileName(folder);
        var at = name.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? name[(at + 1)..] : name;
    }

    // The folder names and pins the current workspace uses and both workspaces' lock files record.
    HashSet<string> KeptFolders()
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in installed)
        {
            _ = kept.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(package.RootPath)));
        }

        foreach (var workspace in (IBrickWorkspace?[])[studio, project?.Workspace])
        {
            if (workspace is null)
            {
                continue;
            }

            try
            {
                foreach (var entry in (IEnumerable<LockEntry>?)LockFile.Load(workspace.Root)?.Dependencies.Values ?? [])
                {
                    if (entry.Commit is { Length: > 0 } commit)
                    {
                        _ = kept.Add(commit);
                    }

                    if (ArchivePin(entry.Integrity) is { } pin)
                    {
                        _ = kept.Add(pin);
                    }
                }
            }
            catch (Exception ex) when (ex is PackageException or IOException)
            {
                log.LogDebug(ex, "The lock file of {Root} could not be read", workspace.Root);
            }
        }

        return kept;
    }

    // The store folder pin of an archive's integrity; null for one that is not an archive hash.
    string? ArchivePin(string? integrity)
    {
        try
        {
            return integrity is { Length: > 0 } && integrity.StartsWith("sha256-", StringComparison.Ordinal)
                ? Store.ArchiveFolderName(integrity)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
