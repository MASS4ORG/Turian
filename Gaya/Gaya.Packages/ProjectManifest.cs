namespace Gaya.Packages;

/// <summary>
/// A project's <c>Packages/manifest.json</c>: the packages it installs, id → source. A git-ignored
/// <c>manifest.user.json</c> beside it can remap entries for one machine, such as pointing a package at a local
/// checkout being worked on.
/// </summary>
public sealed class ProjectManifest
{
    /// <summary>The folder, under the project root, that holds the manifest, the lock file and embedded packages.</summary>
    public const string DirectoryName = "Packages";

    /// <summary>The manifest's file name.</summary>
    public const string FileName = "manifest.json";

    /// <summary>The per-user override's file name.</summary>
    public const string UserFileName = "manifest.user.json";

    /// <summary>
    /// The installed packages: id → <c>file:</c> path (relative to the <c>Packages</c> folder), <c>git+</c> url or
    /// version range.
    /// </summary>
    public Dictionary<string, string?> Dependencies { get; set; } = [];

    /// <summary>
    /// Reads a project's manifest, with its user override applied unless <paramref name="includeUserOverride"/> is
    /// false. A project without a manifest installs nothing.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="includeUserOverride">Whether to apply <c>manifest.user.json</c>.</param>
    /// <returns>The manifest and the ids the user override changed.</returns>
    /// <exception cref="PackageException">A file is unreadable or declares an invalid id.</exception>
    public static (ProjectManifest Manifest, IReadOnlySet<string> Overridden) Load(string projectRoot,
        bool includeUserOverride = true)
    {
        var directory = Path.Combine(projectRoot, DirectoryName);
        var manifest = Read(Path.Combine(directory, FileName)) ?? new ProjectManifest();
        var overridden = new HashSet<string>(StringComparer.Ordinal);

        if (includeUserOverride && Read(Path.Combine(directory, UserFileName)) is { } user)
        {
            // Per-key replace: an entry replaces that id's source; null removes the id.
            foreach (var (id, spec) in user.Dependencies)
            {
                if (spec is null) manifest.Dependencies.Remove(id);
                else manifest.Dependencies[id] = spec;
                overridden.Add(id);
            }
        }

        return (manifest, overridden);
    }

    /// <summary>Writes the manifest into the project's <c>Packages</c> folder.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public void Save(string projectRoot)
    {
        var directory = Path.Combine(projectRoot, DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, PackageJson.Options));
    }

    static ProjectManifest? Read(string path)
    {
        if (!File.Exists(path)) return null;

        ProjectManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path), PackageJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid project manifest: {ex.Message}", ex);
        }

        foreach (var id in manifest?.Dependencies.Keys.Where(static id => !PackageId.IsValid(id)) ?? [])
            throw new PackageException($"{path}: '{id}' is not a valid package id.");

        return manifest;
    }
}
