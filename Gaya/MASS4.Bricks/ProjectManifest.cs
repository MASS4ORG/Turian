namespace MASS4.Bricks;

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

    /// <summary>The file holding personal local links, excluded from locked restores.</summary>
    public const string LinksFileName = "links.json";

    /// <summary>Local folders replacing brick resolutions, relative to this manifest's folder.</summary>
    [Tooltip("Local brick folders, relative to Bricks/manifest.json; bypass registry verification.")]
    public Dictionary<string, string> Links { get; set; } = [];

    /// <summary>
    /// The installed packages: id → <c>file:</c> path (relative to the <c>Packages</c> folder), <c>git+</c> url or
    /// version range.
    /// </summary>
    [Tooltip("Brick id to version range, builtin:, file: or git+ source.")]
    public Dictionary<string, string?> Dependencies { get; set; } = [];

    /// <summary>The registries the project takes bricks from by version range, with optional brick name filters.</summary>
    [Tooltip("Registries and signing keys explicitly trusted by this project.")]
    public List<ScopedRegistry> ScopedRegistries { get; set; } = [];

    /// <summary>
    /// Take each registry brick's bake twin, its prebuilt and imported form, when one is published and trusted, so
    /// nothing of it compiles or imports here. Bricks listed in <see cref="Sources"/> stay source.
    /// </summary>
    [Tooltip("Use bake twins of registry bricks when published and trusted.")]
    public bool Twins { get; set; }

    /// <summary>Bricks taken as source even when <see cref="Twins"/> is on, such as one being fixed.</summary>
    [Tooltip("Bricks kept as source even when twins are on.")]
    public List<string> Sources { get; set; } = [];

    /// <summary>Renames a project's legacy <c>Packages</c> folder to <see cref="DirectoryName"/> when it holds a manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public static void MigrateLegacyLayout(string projectRoot)
    {
        var legacy = Path.Combine(projectRoot, legacyDirectoryName);
        if (Directory.Exists(Path.Combine(projectRoot, DirectoryName))
            || !File.Exists(Path.Combine(legacy, FileName)))
        {
            return;
        }

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
            manifest.ApplyLinks(user.Links, overridden);
            manifest.ApplyDependencies(user.Dependencies, overridden);
            manifest.ApplyRegistries(user.ScopedRegistries);
            manifest.ApplySources(user.Sources, overridden);
        }

        if (includeUserOverride && Read(Path.Combine(directory, LinksFileName)) is { } personal)
        {
            manifest.ApplyLinks(personal.Links, overridden);
        }

        return (manifest, overridden);
    }

    void ApplyLinks(Dictionary<string, string> links, HashSet<string> overridden)
    {
        foreach (var (id, path) in links)
        {
            Links[id] = path;
            overridden.Add(id);
        }
    }

    void ApplyDependencies(Dictionary<string, string?> dependencies, HashSet<string> overridden)
    {
        foreach (var (id, spec) in dependencies)
        {
            if (spec is null)
            {
                Dependencies.Remove(id);
            }
            else
            {
                Dependencies[id] = spec;
            }
            overridden.Add(id);
        }
    }

    void ApplyRegistries(List<ScopedRegistry> registries)
    {
        foreach (var registry in registries)
        {
            ScopedRegistries.RemoveAll(existing => existing.Name == registry.Name);
            ScopedRegistries.Add(registry);
        }
    }

    void ApplySources(List<string> sources, HashSet<string> overridden)
    {
        foreach (var id in sources.Where(id => !Sources.Contains(id)))
        {
            Sources.Add(id);
            overridden.Add(id);
        }
    }

    /// <summary>Writes the manifest into the project's <c>Packages</c> folder.</summary>
    /// <param name="projectRoot">The project folder.</param>
    public void Save(string projectRoot)
    {
        var directory = Path.Combine(projectRoot, DirectoryName);
        Directory.CreateDirectory(directory);
        var node = JsonSerializer.SerializeToNode(this, PackageJson.Options)!.AsObject();
        if (Links.Count == 0)
        {
            node.Remove("links");
        }
        if (ScopedRegistries.Count == 0)
        {
            node.Remove("scopedRegistries");
        }

        if (!Twins)
        {
            node.Remove("twins");
        }

        if (Sources.Count == 0)
        {
            node.Remove("sources");
        }

        File.WriteAllText(Path.Combine(directory, FileName), node.ToJsonString(PackageJson.Options));
    }

    static ProjectManifest? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        ProjectManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path), PackageJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid project manifest: {ex.Message}", ex);
        }

        if (manifest is not null)
        {
            manifest.ValidateLinks(path);
            manifest.ValidateIds(path);
        }

        return manifest;
    }

    void ValidateIds(string path)
    {
        foreach (var id in Dependencies.Keys.Concat(Sources).Concat(Links.Keys).Where(static id => !PackageId.IsValid(id)))
        {
            throw new PackageException($"{path}: '{id}' is not a valid package id.");
        }
    }

    void ValidateLinks(string path)
    {
        if (Links is null)
        {
            throw new PackageException($"{path}: links must be an object mapping brick ids to local folders.");
        }

        foreach (var (id, link) in Links)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                throw new PackageException($"{path}: link '{id}' must name a local folder.");
            }
        }

    }
}
