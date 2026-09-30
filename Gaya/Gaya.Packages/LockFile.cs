namespace Gaya.Packages;

/// <summary>How one package was resolved, as <c>packages-lock.json</c> records it.</summary>
public sealed class LockEntry
{
    /// <summary>The resolved version.</summary>
    public SemanticVersion? Version { get; set; }

    /// <summary>The dependency value it was resolved from, as written; <c>embedded</c> for an embedded package.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The git commit, for a git source.</summary>
    public string? Commit { get; set; }

    /// <summary>The content hash of the store folder, for a git source; local folders change and are not hashed.</summary>
    public string? Integrity { get; set; }

    /// <summary>The fingerprint of the key that signed a package from a registry; later installs must be signed by it too.</summary>
    public string? SignedBy { get; set; }

    /// <summary>1 for a package the project installs itself, deeper for what those depend on.</summary>
    public int Depth { get; set; }

    /// <summary>The package's own dependencies, id → value, as its <c>package.json</c> declares them.</summary>
    public SortedDictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A project's <c>Packages/packages-lock.json</c>: the exact version, commit and content hash each package resolved
/// to, so every machine and build agent installs the same thing until the project updates them.
/// </summary>
public sealed class LockFile
{
    /// <summary>The lock file's name, in the project's <c>Packages</c> folder.</summary>
    public const string FileName = "packages-lock.json";

    /// <summary>The resolved packages, by id.</summary>
    public SortedDictionary<string, LockEntry> Dependencies { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads a project's lock file; null when it has none.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The lock file, or null.</returns>
    /// <exception cref="PackageException">The file is unreadable.</exception>
    public static LockFile? Load(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ProjectManifest.DirectoryName, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), PackageJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{path} is not a valid lock file: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the lock file into the project's <c>Packages</c> folder, only when it changed.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>True when the file was written.</returns>
    public bool Save(string projectRoot)
    {
        var directory = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var path = Path.Combine(directory, FileName);
        var json = Serialize();
        if (File.Exists(path) && File.ReadAllText(path) == json) return false;

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, json);
        return true;
    }

    /// <summary>The file's text.</summary>
    /// <returns>The JSON.</returns>
    public string Serialize() => JsonSerializer.Serialize(this, PackageJson.Options);
}
