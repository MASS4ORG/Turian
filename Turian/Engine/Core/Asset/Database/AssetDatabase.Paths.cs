namespace Turian.Engine.Core;

public sealed partial class AssetDatabase
{

    static IAssetFileProvider? CreateProvider(AssetRecord record)
    {
        return record.StorageKind switch
        {
            AssetStorageKind.LooseFile => CreateLooseFileProvider(record),
            AssetStorageKind.Oap => CreateOapFileProvider(record),
            _ => CreateLooseFileProvider(record)
        };
    }

    static IAssetFileProvider? CreateLooseFileProvider(AssetRecord record)
    {
        var resolvedPath = record.ResolveContentPath();
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        return new RealFileSystemProvider(resolvedPath);
    }

    static IAssetFileProvider? CreateOapFileProvider(AssetRecord record)
    {
        var oapPath = record.ResolveContentPath();
        if (string.IsNullOrWhiteSpace(oapPath))
        {
            return null;
        }

        return new OapAssetFileProvider(oapPath, record.PrimaryContentKey);
    }

    static AssetCatalog ReadCatalog(string path) => ReadCatalog(path, out _);

    /// <summary>
    /// Reads a catalog file, reporting through <paramref name="status"/> whether it was absent or
    /// present-but-unusable. The two cases look identical in the returned catalog — both are empty —
    /// but they are not the same thing: a project with no catalog yet has simply not been imported,
    /// while a catalog that fails to parse means the cache is damaged and the assets it indexed are
    /// about to silently disappear from the editor.
    /// </summary>
    static AssetCatalog ReadCatalog(string path, out AssetCatalogLoadStatus status)
    {
        if (!File.Exists(path))
        {
            status = AssetCatalogLoadStatus.Missing;
            return new AssetCatalog();
        }

        try
        {
            // A truncated catalog — a zero-byte file left by a crash or a full disk mid-write — either
            // throws here or deserialises to null, depending on how much of it survived.
            if (Serializer.Load<AssetCatalog>(path) is { } catalog)
            {
                status = AssetCatalogLoadStatus.Loaded;
                return catalog;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through to Unreadable.
        }

        status = AssetCatalogLoadStatus.Unreadable;
        return new AssetCatalog();
    }

    static void SaveCatalog(AssetCatalog catalog, string path)
    {
        EnsureParentDirectory(path);
        Serializer.Save(path, catalog);
    }

    static string GetProjectCatalogPath(string projectRoot)
    {
        return Path.Combine(projectRoot, cacheDirectoryName, cacheCatalogFileName);
    }

    /// <summary>
    /// Reports whether <paramref name="artifactPath"/> is the <c>primary.&lt;target&gt;.&lt;ext&gt;</c>
    /// variant baked for <paramref name="target"/>.
    /// </summary>
    static bool IsArtifactForTarget(string artifactPath, string target) =>
        string.Equals(ArtifactTarget(artifactPath), target, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the build target from a <c>primary.&lt;target&gt;.&lt;ext&gt;</c> artifact name, or
    /// <c>null</c> when the artifact is not target-specific.
    /// </summary>
    static string? ArtifactTarget(string artifactPath)
    {
        var fileName = Path.GetFileName(artifactPath);
        var parts = fileName.Split('.');
        return parts.Length == 3 && parts[0].Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    /// <summary>
    /// Maps each build target present in an asset's import directory to its project-relative
    /// artifact path. Empty for assets whose single artifact serves every target.
    /// </summary>
    static Dictionary<string, string> ResolveTargetArtifacts(
        string projectRoot,
        Guid assetId,
        string? importedAssetsRoot)
    {
        var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (assetId == Guid.Empty || string.IsNullOrWhiteSpace(importedAssetsRoot) || !Directory.Exists(importedAssetsRoot))
        {
            return artifacts;
        }

        var assetImportDirectory = Path.Combine(importedAssetsRoot, "by-guid", assetId.ToString("N"));
        if (!Directory.Exists(assetImportDirectory))
        {
            return artifacts;
        }

        foreach (var file in Directory.GetFiles(assetImportDirectory, "primary.*", SearchOption.TopDirectoryOnly))
        {
            if (ArtifactTarget(file) is { } target)
            {
                artifacts[target] = TryMakeRelativeProjectPath(projectRoot, file);
            }
        }

        return artifacts;
    }

    static string? ResolveImportedRelativePathFromCache(Guid assetId, string? importedAssetsRoot)
    {
        var directory = GetAssetCacheDirectory(assetId, importedAssetsRoot);
        return directory is null ? null : ReadManifestPrimary(directory) ?? FindPrimaryArtifact(directory);
    }

    static string? GetAssetCacheDirectory(Guid assetId, string? importedAssetsRoot)
    {
        if (assetId == Guid.Empty || string.IsNullOrWhiteSpace(importedAssetsRoot) || !Directory.Exists(importedAssetsRoot))
        {
            return null;
        }

        var assetImportDirectory = Path.Combine(importedAssetsRoot, "by-guid", assetId.ToString("N"));
        if (!Directory.Exists(assetImportDirectory))
        {
            return null;
        }

        return assetImportDirectory;
    }

    static string? FindPrimaryArtifact(string assetImportDirectory)
    {
        // An importer that bakes per-target variants names them primary.<target>.<ext>; prefer the
        // running platform's, and fall back to plain alphabetical order for single-artifact assets.
        var preferredFiles = Directory
            .GetFiles(assetImportDirectory, "primary*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => IsArtifactForTarget(path, TextureBuildTarget.Current))
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedFile = preferredFiles.FirstOrDefault()
            ?? Directory.GetFiles(assetImportDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(static file => !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        return selectedFile;
    }

    static string? ReadManifestPrimary(string directory)
    {
        var manifestPath = Path.Combine(directory, "import.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            return ResolveManifestPrimary(directory, document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? ResolveManifestPrimary(string directory, JsonElement manifest)
    {
        if (ManifestPrimaryFileName(manifest) is not { } fileName) return null;
        var path = Path.Combine(directory, fileName);
        if (ArtifactTarget(path) is { } target && target != TextureBuildTarget.Current) return null;
        return File.Exists(path) ? path : null;
    }

    static string? ManifestPrimaryFileName(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) return null;
        if (!manifest.TryGetProperty("PrimaryArtifactFileName", out var property)
            || property.ValueKind != JsonValueKind.String) return null;
        var fileName = property.GetString();
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return null;
        return fileName;
    }

    static string ResolveImportedRelativePath(
        string projectRoot,
        string sourcePath,
        string? importedPrimaryContentPath,
        string? fallbackImportedRelativePath = null)
    {
        if (!string.IsNullOrWhiteSpace(importedPrimaryContentPath))
        {
            return TryMakeRelativeProjectPath(projectRoot, Path.GetFullPath(importedPrimaryContentPath));
        }

        if (!string.IsNullOrWhiteSpace(fallbackImportedRelativePath))
        {
            return Path.IsPathRooted(fallbackImportedRelativePath)
                ? TryMakeRelativeProjectPath(projectRoot, fallbackImportedRelativePath)
                : TryMakeRelativeProjectPath(projectRoot, Path.GetFullPath(fallbackImportedRelativePath));
        }

        return TryMakeRelativeProjectPath(projectRoot, sourcePath);
    }

    static List<string> ResolveArtifacts(
        string projectRoot,
        string sourcePath,
        string? importedPrimaryContentPath,
        string? fallbackImportedRelativePath)
    {
        var importedRelativePath = ResolveImportedRelativePath(
            projectRoot,
            sourcePath,
            importedPrimaryContentPath,
            fallbackImportedRelativePath);

        return string.IsNullOrWhiteSpace(importedRelativePath)
            ? []
            : [importedRelativePath];
    }

    static string GetAssetPathFromMeta(string metaFilePath)
    {
        return metaFilePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? metaFilePath[..^".meta".Length]
            : metaFilePath;
    }

    /// <summary>The project installing the package whose folder holds <paramref name="path"/>; null outside packages.</summary>
    string? PackageProjectRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return packageRoots.Any(root => IsUnderDirectory(fullPath, root)) ? packagesProjectRoot : null;
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="directory"/>.</summary>
    /// <param name="path">An absolute path.</param>
    /// <param name="directory">An absolute folder.</param>
    /// <returns>True when inside.</returns>
    public static bool IsUnderDirectory(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Whether a path inside <paramref name="root"/> sits in a folder whose name ends in <c>~</c>, which packages use
    /// for content that is never imported (samples, documentation).
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <param name="root">The package folder.</param>
    /// <returns>True when the path is in such a folder.</returns>
    public static bool IsInTildeFolder(string path, string root) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1)
            .Any(static segment => segment.EndsWith('~'));

    static string? TryResolveProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);

        var directory = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Combine(directory, "Assets")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    static string TryMakeRelativeProjectPath(string? projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalizedPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return normalizedPath;
        }

        try
        {
            return Path.GetRelativePath(projectRoot, normalizedPath);
        }
        catch
        {
            return normalizedPath;
        }
    }

    static void EnsureParentDirectory(string path)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    static AssetRecord CloneRecord(AssetRecord record)
    {
        return new AssetRecord
        {
            AssetId = record.AssetId,
            ParentAssetId = record.ParentAssetId,
            ProjectRootPath = record.ProjectRootPath,
            AssetTypeName = record.AssetTypeName,
            DataAssetPayloadTypeId = record.DataAssetPayloadTypeId,
            Labels = [.. record.Labels],
            SourceRelativePath = record.SourceRelativePath,
            MetaRelativePath = record.MetaRelativePath,
            PrimaryContentKey = record.PrimaryContentKey,
            ImportedRelativePath = record.ImportedRelativePath,
            StorageKind = record.StorageKind,
            SourceHash = record.SourceHash,
            SettingsHash = record.SettingsHash,
            ImporterId = record.ImporterId,
            ImporterVersion = record.ImporterVersion,
            Artifacts = [.. record.Artifacts],
            TargetArtifacts = new Dictionary<string, string>(record.TargetArtifacts, StringComparer.OrdinalIgnoreCase),
            ComponentTypes = [.. record.ComponentTypes]
        };
    }

    static Asset? TryLoadAssetMetadata(string metaFilePath)
    {
        if (string.IsNullOrWhiteSpace(metaFilePath) || !File.Exists(metaFilePath))
        {
            return null;
        }

        try
        {
            return Asset.Load(metaFilePath);
        }
        catch
        {
            return null;
        }
    }
}
