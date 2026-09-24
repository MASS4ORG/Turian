namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    string ResolveImporterId(string sourceFilePath)
    {
        foreach (var importer in assetImporters)
        {
            if (importer.IsValid(sourceFilePath))
            {
                return importer.GetType().FullName ?? importer.GetType().Name;
            }
        }

        return nameof(GenericAssetImporter);
    }

    void ClearImportDirectory(string importDirectory)
    {
        if (!Directory.Exists(importDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(importDirectory))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.GetDirectories(importDirectory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    string GetAssetImportDirectory(Guid assetId)
    {
        if (cacheAssetsRootPath is null)
        {
            throw new InvalidOperationException("Asset cache paths were not initialized.");
        }

        return Path.Combine(
            cacheAssetsRootPath,
            cacheByGuidDirectoryName,
            assetId.ToString("N"));
    }

    void CleanupImportedArtifactsForAssetPath(string assetPath)
    {
        var metaPath = GetMetaFilePath(assetPath);
        CleanupImportedArtifactsForMetaPath(metaPath);
    }

    void CleanupImportedArtifactsForMetaPath(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return;
        }

        try
        {
            var asset = Asset.Load(metaPath);
            if (asset is not null)
            {
                CleanupImportedArtifactsForAsset(asset);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean imported artifacts for meta file {MetaPath}", metaPath);
        }
    }

    void CleanupImportedArtifactsForAsset(Asset asset)
    {
        // The children live inside the import directory and have no meta of their own,
        // so nothing else would ever remove their catalog records.
        assetDatabase.RemoveChildAssets(asset.Id);

        var importDirectory = GetAssetImportDirectory(asset.Id);
        if (!Directory.Exists(importDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(importDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete imported asset directory {ImportDirectory}", importDirectory);
        }
    }

    void ApplyTypedDefaults(Asset asset, string filePath)
    {
        if (asset is Prefab)
        {
            return;
        }

        if (asset is ModelImportAsset modelImportAsset)
        {
            modelImportAsset.ImportSettings.Format = Path.GetExtension(filePath)
                .TrimStart('.')
                .ToUpperInvariant();
            return;
        }

        if (asset is ModelAssetMeta modelMeta)
        {
            modelMeta.ModelFormat = ModelAssetFormatExtensions.FromFilePath(filePath);
            return;
        }

        if (asset is TextureAssetMeta textureMeta)
        {
            textureMeta.TextureFormat = TextureAssetFormatExtensions.FromFilePath(filePath);
            return;
        }

        if (asset is TextureAsset texture)
        {
            // Stamp the project default into the meta at creation time rather than reading it at
            // bake time: the settings hash is computed from the meta, so a value left outside it
            // would never trigger the reimport that applying it requires. Changing the project
            // default therefore affects newly-imported textures only.
            if (texture.ImportSettings.MaxResolution == 0)
            {
                texture.ImportSettings.MaxResolution =
                    settingsService.Settings?.Get<GraphicsSettings>().TextureMaxResolution ?? 0;
            }

            return;
        }

        if (asset is SoundAssetMeta soundMeta)
        {
            soundMeta.SoundFormat = SoundAssetFormatExtensions.FromFilePath(filePath);
        }
    }

    static Asset CreateDefaultAsset(string filePath)
    {
        if (IsSceneAssetPath(filePath))
        {
            return new Prefab
            {
                RelativePath = filePath
            };
        }

        return new Asset
        {
            RelativePath = filePath
        };
    }

    static bool IsSceneAssetPath(string filePath)
    {
        return filePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    void RebuildDatabase()
    {
        if (string.IsNullOrWhiteSpace(assetsRootPath))
        {
            return;
        }

        assetDatabase.BuildDatabase(assetsRootPath);
    }

    void PersistCacheCatalog()
    {
        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            return;
        }

        assetDatabase.SaveCatalog(projectRootPath);
    }

    static bool IsMetaFilePath(string filePath)
    {
        return filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }

    static string GetMetaFilePath(string assetPath)
    {
        return $"{assetPath}.meta";
    }

    static string GetAssetPathFromMeta(string metaFilePath)
    {
        return metaFilePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? metaFilePath[..^".meta".Length]
            : metaFilePath;
    }

    bool ShouldIgnorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        if (HasIgnoredExtension(path))
        {
            return true;
        }

        if (assetsRootPath is null)
        {
            return false;
        }

        return !IsUnderAssetsRoot(path);
    }

    bool IsUnderAssetsRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(assetsRootPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(assetsRootPath, StringComparison.OrdinalIgnoreCase);
    }

    bool ShouldIgnoreMetaPath(string metaPath)
    {
        var assetPath = GetAssetPathFromMeta(metaPath);
        return HasIgnoredExtension(assetPath) || !IsUnderAssetsRoot(assetPath);
    }

    void DeleteMetaFile(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return;
        }

        try
        {
            File.Delete(metaPath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to delete asset meta file {MetaFilePath}", metaPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Access denied while deleting asset meta file {MetaFilePath}", metaPath);
        }
    }

    static bool HasIgnoredExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return ignoredExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    string ToProjectRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            return absolutePath;
        }

        return Path.GetRelativePath(projectRootPath, absolutePath);
    }

    static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes);
    }

    static string ComputeStringHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        folderWatcher?.Dispose();
        folderWatcher = null;
    }

    // ── Inner watcher ──────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="ProjectDirectoryWatcher"/> that delegates all file-system events
    /// back to the enclosing <see cref="AssetImporter"/> instance.
    /// </summary>
    sealed class AssetFolderWatcher(ILogger logger, AssetImporter owner)
        : ProjectDirectoryWatcher(logger)
    {
        /// <inheritdoc/>
        protected override NotifyFilters WatcherNotifyFilters =>
            NotifyFilters.FileName
            | NotifyFilters.DirectoryName
            | NotifyFilters.LastWrite
            | NotifyFilters.CreationTime;

        /// <inheritdoc/>
        protected override void OnFileCreated(FileSystemEventArgs e) => owner.OnWatcherCreated(e);

        /// <inheritdoc/>
        protected override void OnFileChanged(FileSystemEventArgs e) => owner.OnWatcherChanged(e);

        /// <inheritdoc/>
        protected override void OnFileDeleted(FileSystemEventArgs e) => owner.OnWatcherDeleted(e);

        /// <inheritdoc/>
        protected override void OnFileRenamed(RenamedEventArgs e) => owner.OnWatcherRenamed(e);
    }
}
