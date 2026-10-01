namespace Gaya.Packages;

/// <summary>
/// A project's <c>Bricks/manifest.json</c>: the packages it installs, id → source. A git-ignored
/// <c>manifest.user.json</c> beside it can remap entries for one machine, such as pointing a package at a local
/// checkout being worked on.
/// </summary>
public class ProjectManifest
{
    /// <summary>The folder, under the project root, that holds the manifest, the lock file and embedded packages.</summary>
    public const string DirectoryName = "Bricks";

    /// <summary>The folder earlier versions used in place of <see cref="DirectoryName"/>.</summary>
    const string legacyDirectoryName = "Packages";

    /// <summary>The manifest's file name.</summary>
    public const string FileName = "manifest.json";

    /// <summary>The per-user override's file name.</summary>
    public const string UserFileName = "manifest.user.json";

    /// <summary>
    /// The installed packages: id → <c>file:</c> path (relative to the <c>Packages</c> folder), <c>git+</c> url or
    /// version range.
    /// </summary>
    [Tooltip("Brick id to version range, builtin:, file: or git+ source.")]
    public Dictionary<string, string?> Dependencies { get; set; } = [];

    /// <summary>The registries the project takes bricks from by version range, for the names each is scoped to.</summary>
    [Tooltip("Registries and signing keys explicitly trusted by this project.")]
    public List<ScopedRegistry> ScopedRegistries { get; set; } = [];

    /// <summary>Renames a project's legacy <c>Packages</c> folder to <see cref="DirectoryName"/> when it holds a manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public static void MigrateLegacyLayout(string projectRoot)
    {
        var legacy = Path.Combine(projectRoot, legacyDirectoryName);
        if (Directory.Exists(Path.Combine(projectRoot, DirectoryName))
            || !File.Exists(Path.Combine(legacy, FileName))) return;

        Directory.Move(legacy, Path.Combine(projectRoot, DirectoryName));
    }

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
        MigrateLegacyLayout(projectRoot);
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

            // A registry of the same name replaces the project's; others are added.
            foreach (var registry in user.ScopedRegistries)
            {
                manifest.ScopedRegistries.RemoveAll(existing => existing.Name == registry.Name);
                manifest.ScopedRegistries.Add(registry);
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
        var node = JsonSerializer.SerializeToNode(this, PackageJson.Options)!.AsObject();
        if (ScopedRegistries.Count == 0) node.Remove("scopedRegistries");
        File.WriteAllText(Path.Combine(directory, FileName), node.ToJsonString(PackageJson.Options));
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
