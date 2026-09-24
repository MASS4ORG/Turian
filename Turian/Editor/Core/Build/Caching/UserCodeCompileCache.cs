namespace Turian.Editor.Core;

/// <summary>
/// Persists and validates a cache manifest for compiled user code.
///
/// <para>
/// The manifest represents the exact editor-side inputs that affect the generated user assembly:
/// source files under <c>Assets/</c>, project settings, package references, and build configuration.
/// If all tracked inputs match and the output assembly still exists, recompilation can be skipped.
/// </para>
/// </summary>
public sealed class UserCodeCompileCache(ILogger logger)
{
    const string cacheDirectoryName = ".Cache";
    const string manifestDirectoryName = "Build";
    const string manifestFilePrefix = "usercode.compile";
    const string manifestFileSuffix = ".cache.json";

    readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Builds a snapshot of the current compilation inputs for the given project settings.
    /// </summary>
    public UserCodeCompileCacheManifest CreateManifest(
        IBuildAppSettings settings,
        string assemblyOutputPath,
        string csprojFilePath,
        BuildConfiguration configuration = BuildConfiguration.Debug)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(csprojFilePath);

        var sourceFiles = EnumerateSourceFiles(settings.AssetsAbsoluteDir);

        var sourceEntries = sourceFiles
            .Select(path => CreateSourceEntry(settings.ProjectAbsoluteDir, path))
            .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var player = settings.Get<PlayerSettings>();
        var manifest = new UserCodeCompileCacheManifest
        {
            ProjectPath = settings.ProjectAbsoluteDir,
            AssemblyOutputPath = Path.GetFullPath(assemblyOutputPath),
            CsProjFilePath = Path.GetFullPath(csprojFilePath),
            Configuration = configuration.ToString(),
            TargetFramework = settings.TargetFramework,
            TargetSdk = settings.TargetSdk,
            Title = settings.Title,
            ProductName = player.ProductName,
            Author = player.Author,
            ApplicationIdentifier = player.ApplicationIdentifier,
            Version = player.Version,
            StartupScene = player.StartupScene,
            PackageReferences = [.. settings.PackageReferences
                .Select(static package => new PackageReferenceEntry(package.Item1, package.Item2))
                .OrderBy(static package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)],
            TurianPackages = [.. settings.TurianPackages
                .Select(static package => new InternalPackageReferenceEntry(package.Item1, package.Item2))
                .OrderBy(static package => package.ProjectPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.AssemblyName, StringComparer.OrdinalIgnoreCase)],
            SourceFiles = sourceEntries,
            SourceFingerprint = ComputeSourceFingerprint(sourceEntries),
            SettingsFingerprint = ComputeSettingsFingerprint(settings),
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        return manifest;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the cached assembly and manifest still match
    /// the current project inputs and can be safely reused.
    /// Pass <paramref name="forceRecompile"/> as <see langword="true"/> to bypass the cache unconditionally.
    /// </summary>
    public bool IsAssemblyUpToDate(
        IBuildAppSettings settings,
        string assemblyOutputPath,
        string csprojFilePath,
        BuildConfiguration configuration = BuildConfiguration.Debug,
        bool forceRecompile = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(csprojFilePath);

        if (forceRecompile)
        {
            logger.LogDebug("Compile cache bypassed: force recompile requested");
            return false;
        }

        var normalizedAssemblyPath = Path.GetFullPath(assemblyOutputPath);
        if (!File.Exists(normalizedAssemblyPath))
        {
            logger.LogDebug("Compile cache miss: output assembly not found at {AssemblyOutputPath}", normalizedAssemblyPath);
            return false;
        }

        var manifestPath = GetManifestPath(settings.ProjectAbsoluteDir, normalizedAssemblyPath);
        if (!File.Exists(manifestPath))
        {
            logger.LogDebug("Compile cache miss: manifest not found at {ManifestPath}", manifestPath);
            return false;
        }

        UserCodeCompileCacheManifest? persisted;
        try
        {
            persisted = Serializer.Load<UserCodeCompileCacheManifest>(manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read compile cache manifest at {ManifestPath}", manifestPath);
            return false;
        }

        if (persisted is null)
        {
            logger.LogDebug("Compile cache miss: manifest deserialized as null at {ManifestPath}", manifestPath);
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(persisted.CsProjFilePath ?? string.Empty),
                Path.GetFullPath(csprojFilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Compile cache miss: generated project path changed");
            return false;
        }

        if (!string.Equals(persisted.Configuration, configuration.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Compile cache miss: build configuration changed");
            return false;
        }

        var current = CreateManifest(settings, normalizedAssemblyPath, csprojFilePath, configuration);

        if (!string.Equals(persisted.TargetFramework, current.TargetFramework, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Compile cache miss: target framework changed ({Old} → {New})", persisted.TargetFramework, current.TargetFramework);
            return false;
        }

        if (!string.Equals(persisted.TargetSdk, current.TargetSdk, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Compile cache miss: target SDK changed ({Old} → {New})", persisted.TargetSdk, current.TargetSdk);
            return false;
        }

        if (!string.Equals(persisted.SettingsFingerprint, current.SettingsFingerprint, StringComparison.Ordinal))
        {
            logger.LogDebug("Compile cache miss: project settings changed");
            return false;
        }

        if (!string.Equals(persisted.SourceFingerprint, current.SourceFingerprint, StringComparison.Ordinal))
        {
            logger.LogDebug("Compile cache miss: source file fingerprint changed");
            return false;
        }

        if (!PackageReferencesEqual(persisted.PackageReferences, current.PackageReferences))
        {
            logger.LogDebug("Compile cache miss: package references changed");
            return false;
        }

        if (!InternalPackagesEqual(persisted.TurianPackages, current.TurianPackages))
        {
            logger.LogDebug("Compile cache miss: internal package references changed");
            return false;
        }

        if (!SourceEntriesEqual(persisted.SourceFiles, current.SourceFiles))
        {
            logger.LogDebug("Compile cache miss: source file set or hashes changed");
            return false;
        }

        logger.LogDebug("Compile cache hit: assembly is still valid at {AssemblyOutputPath}", normalizedAssemblyPath);
        return true;
    }

    /// <summary>
    /// Saves the manifest for the given assembly output path inside the project's cache folder.
    /// Each assembly slot (A/B) gets its own manifest file.
    /// </summary>
    public void SaveManifest(string projectRootPath, string assemblyOutputPath, UserCodeCompileCacheManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyOutputPath);
        ArgumentNullException.ThrowIfNull(manifest);

        var manifestPath = GetManifestPath(projectRootPath, assemblyOutputPath);
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Serializer.Save(manifestPath, manifest);
        logger.LogDebug("Compile cache manifest saved to {ManifestPath}", manifestPath);
    }

    /// <summary>
    /// Deletes the persisted manifest for the given assembly output path, if it exists.
    /// </summary>
    public void Invalidate(string projectRootPath, string assemblyOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyOutputPath);

        var manifestPath = GetManifestPath(projectRootPath, assemblyOutputPath);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            File.Delete(manifestPath);
            logger.LogDebug("Compile cache manifest invalidated at {ManifestPath}", manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete compile cache manifest at {ManifestPath}", manifestPath);
        }
    }

    /// <summary>
    /// Gets the absolute path to the compile cache manifest for the specified project root and assembly output path.
    /// Each assembly slot (A/B) gets its own manifest so slot rotation does not invalidate the cache.
    /// </summary>
    public static string GetManifestPath(string projectRootPath, string assemblyOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyOutputPath);

        var slotName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(assemblyOutputPath)))
                       ?? "default";
        var manifestFileName = $"{manifestFilePrefix}.{slotName}{manifestFileSuffix}";

        return Path.Combine(
            Path.GetFullPath(projectRootPath),
            cacheDirectoryName,
            manifestDirectoryName,
            manifestFileName);
    }

    static IEnumerable<string> EnumerateSourceFiles(string assetsAbsoluteDir)
    {
        if (string.IsNullOrWhiteSpace(assetsAbsoluteDir) || !Directory.Exists(assetsAbsoluteDir))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(assetsAbsoluteDir, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}.Cache{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{Path.AltDirectorySeparatorChar}.Cache{Path.AltDirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
    }

    static SourceFileCacheEntry CreateSourceEntry(string projectRootPath, string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);

        return new SourceFileCacheEntry
        {
            RelativePath = Path.GetRelativePath(projectRootPath, fullPath),
            FullPath = fullPath,
            Hash = ComputeFileHash(fullPath),
            Length = new FileInfo(fullPath).Length,
        };
    }

    static string ComputeSourceFingerprint(IEnumerable<SourceFileCacheEntry> entries)
    {
        var builder = new StringBuilder();

        foreach (var entry in entries.OrderBy(static x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.RelativePath).Append('|')
                .Append(entry.Hash).Append('|')
                .Append(entry.Length).AppendLine();
        }

        return ComputeStringHash(builder.ToString());
    }

    static string ComputeSettingsFingerprint(IBuildAppSettings settings)
    {
        var builder = new StringBuilder();
        var player = settings.Get<PlayerSettings>();

        builder.AppendLine(settings.ProjectAbsoluteDir);
        builder.AppendLine(settings.AssetsAbsoluteDir);
        builder.AppendLine(settings.TargetFramework);
        builder.AppendLine(settings.TargetSdk);
        builder.AppendLine(settings.Title ?? string.Empty);
        builder.AppendLine(player.ProductName ?? string.Empty);
        builder.AppendLine(player.Author ?? string.Empty);
        builder.AppendLine(player.ApplicationIdentifier ?? string.Empty);
        builder.AppendLine(player.Version ?? string.Empty);
        builder.AppendLine(player.StartupScene?.AssetId.ToString() ?? string.Empty);

        foreach (var package in settings.PackageReferences
                     .OrderBy(static x => x.Item1, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static x => x.Item2, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(package.Item1).Append('|').Append(package.Item2).AppendLine();
        }

        foreach (var package in settings.TurianPackages
                     .OrderBy(static x => x.Item1, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static x => x.Item2, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(package.Item1).Append('|').Append(package.Item2).AppendLine();
        }

        return ComputeStringHash(builder.ToString());
    }

    static bool SourceEntriesEqual(
        IReadOnlyList<SourceFileCacheEntry>? left,
        IReadOnlyList<SourceFileCacheEntry>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].RelativePath, right[i].RelativePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[i].Hash, right[i].Hash, StringComparison.Ordinal)
                || left[i].Length != right[i].Length)
            {
                return false;
            }
        }

        return true;
    }

    static bool PackageReferencesEqual(
        IReadOnlyList<PackageReferenceEntry>? left,
        IReadOnlyList<PackageReferenceEntry>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[i].Version, right[i].Version, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    static bool InternalPackagesEqual(
        IReadOnlyList<InternalPackageReferenceEntry>? left,
        IReadOnlyList<InternalPackageReferenceEntry>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].ProjectPath, right[i].ProjectPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[i].AssemblyName, right[i].AssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    static string ComputeStringHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

/// <summary>
/// Serialized manifest representing the inputs and outputs of the latest successful user-code compilation.
/// </summary>
[PublicAPI]
public sealed class UserCodeCompileCacheManifest
{
    /// <summary>Gets or sets the project file path.</summary>
    public string? ProjectPath { get; set; }
    /// <summary>Gets or sets the compiled assembly output path.</summary>
    public string? AssemblyOutputPath { get; set; }
    /// <summary>Gets or sets the generated .csproj file path.</summary>
    public string? CsProjFilePath { get; set; }
    /// <summary>Gets or sets the build configuration (e.g., Debug or Release).</summary>
    public string? Configuration { get; set; }
    /// <summary>Gets or sets the target framework moniker.</summary>
    public string? TargetFramework { get; set; }
    /// <summary>Gets or sets the target SDK version.</summary>
    public string? TargetSdk { get; set; }
    /// <summary>Gets or sets the project title.</summary>
    public string? Title { get; set; }
    /// <summary>Gets or sets the product name.</summary>
    public string? ProductName { get; set; }
    /// <summary>Gets or sets the company name.</summary>
    public string? Author { get; set; }
    /// <summary>Gets or sets the application identifier.</summary>
    public string? ApplicationIdentifier { get; set; }
    /// <summary>Gets or sets the project version.</summary>
    public string? Version { get; set; }
    /// <summary>Gets or sets the startup scene reference.</summary>
    public AssetReference<Prefab>? StartupScene { get; set; }
    /// <summary>Gets or sets the settings fingerprint for change detection.</summary>
    public string? SettingsFingerprint { get; set; }
    /// <summary>Gets or sets the source fingerprint for change detection.</summary>
    public string? SourceFingerprint { get; set; }
    /// <summary>Gets or sets the generation timestamp in UTC.</summary>
    public DateTimeOffset GeneratedAtUtc { get; set; }
    /// <summary>Gets or sets the list of NuGet package references.</summary>
    public List<PackageReferenceEntry> PackageReferences { get; set; } = [];
    /// <summary>Gets or sets the list of internal Turian package references.</summary>
    public List<InternalPackageReferenceEntry> TurianPackages { get; set; } = [];
    /// <summary>Gets or sets the list of cached source files.</summary>
    public List<SourceFileCacheEntry> SourceFiles { get; set; } = [];
}

/// <summary>
/// Cached representation of a single source file under <c>Assets/</c>.
/// </summary>
[PublicAPI]
public sealed class SourceFileCacheEntry
{
    /// <summary>Gets or sets the relative path of the source file.</summary>
    public string RelativePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the full path of the source file.</summary>
    public string FullPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the SHA-256 hash of the source file content.</summary>
    public string Hash { get; set; } = string.Empty;
    /// <summary>Gets or sets the length of the source file in bytes.</summary>
    public long Length { get; set; }
}

/// <summary>
/// Cached representation of a NuGet package reference.
/// </summary>
public sealed record PackageReferenceEntry(string Name, string Version);

/// <summary>
/// Cached representation of an internal Turian package reference.
/// </summary>
public sealed record InternalPackageReferenceEntry(string ProjectPath, string AssemblyName);
