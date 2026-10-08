namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    /// <summary>
    /// Synchronously imports the asset at <paramref name="filePath"/> into the cache,
    /// creating or updating its meta file and asset-database record. Use this when
    /// the editor has just written the file to disk and the play runtime must read
    /// the latest content immediately, bypassing the file-watcher delay.
    /// </summary>
    public void ReimportNow(string filePath, bool overwriteExisting = true)
    {
        lock (syncRoot)
        {
            EnsureAssetImported(filePath, overwriteExisting);
        }
    }

    void EnsureAssetImported(string filePath, bool overwriteExisting)
    {
        if (!CanImportSource(filePath)) return;

        var metaFilePath = GetMetaFilePath(filePath);

        // A store package is shared and read-only: it must ship its metas, and its files never change.
        if (TryRegisterReadOnlyAsset(filePath, metaFilePath)) return;

        if (!overwriteExisting && File.Exists(metaFilePath))
        {
            if (TryRegisterMetadata(metaFilePath)) return;
        }

        Asset asset;
        try
        {
            asset = CreateOrLoadAssetMetadata(filePath, metaFilePath);
        }
        catch (UnresolvableTypeIdException ex)
        {
            WarnUnavailableType(metaFilePath, ex);
            return;
        }

        SaveAssetMetadata(asset, metaFilePath);

        if (!TryImportAssetToCache(asset, filePath, out _, out _)) return;

        logger.LogInformation(
            "Asset metadata {Action}: {MetaFilePath}",
            overwriteExisting ? "updated" : "created",
            metaFilePath);

        FinishAssetImport(asset, filePath, notify: true);
    }

    bool TryRegisterMetadata(string path)
    {
        try { return RegisterExistingMetaFile(path); }
        catch (UnresolvableTypeIdException ex)
        {
            WarnUnavailableType(path, ex);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to register asset meta file {MetaFilePath}. Overwriting malformed metadata", path);
            return false;
        }
    }

    static void SaveAssetMetadata(Asset asset, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, SerializeAssetMetadata(asset));
    }

    bool CanImportSource(string path) =>
        !ShouldIgnorePath(path) && !IsMetaFilePath(path) && File.Exists(path);

    bool TryRegisterReadOnlyAsset(string path, string metaPath)
    {
        if (PackageRootOf(path) is not { ReadOnly: true }) return false;
        if (File.Exists(metaPath)) RegisterExistingMetaFile(metaPath);
        else logger.LogWarning("Package asset {FilePath} has no meta file and cannot be given one; it is skipped", path);
        return true;
    }

    /// <summary>
    /// The type comes from a brick or assembly that is not installed; rewriting the meta would lose its id, so the
    /// asset is left as it is.
    /// </summary>
    void WarnUnavailableType(string metaFilePath, UnresolvableTypeIdException ex) =>
        logger.LogWarning(
            "Asset meta file {MetaFilePath} names type {TypeId}, which nothing provides; the asset is skipped",
            metaFilePath,
            ex.TypeId);

    /// <summary>
    /// Completes an import: rebuilds the database, registers the asset's children and persists the
    /// catalog. Inside a folder scan the work is deferred so the whole scan pays for it once.
    /// </summary>
    void FinishAssetImport(Asset asset, string sourceFilePath, bool notify)
    {
        if (pendingBatch is not null)
        {
            pendingBatch.Add((asset, sourceFilePath));
            return;
        }

        RebuildDatabase();
        RegisterChildAssets(asset, sourceFilePath);
        RefreshPrefabComponentIndex(asset, ResolveImportedPrimaryPath(asset.Id));
        SaveIndexStamp(asset.Id);
        PersistCacheCatalog();

        if (notify)
        {
            NotifyAssetsChanged();
        }
    }

    bool RegisterExistingMetaFile(string metaFilePath)
    {
        if (!File.Exists(metaFilePath))
        {
            return false;
        }

        var asset = Asset.Load(metaFilePath);
        if (asset is null)
        {
            logger.LogWarning("Could not deserialize asset meta file {MetaFilePath}", metaFilePath);
            return false;
        }

        var assetPath = GetAssetPathFromMeta(metaFilePath);
        asset = ApplyImportOverrides(asset, assetPath);
        asset.RelativePath = assetPath;

        if (!File.Exists(assetPath))
        {
            CleanupImportedArtifactsForAsset(asset);
            RebuildDatabase();
            return false;
        }

        if (!TryImportAssetToCache(asset, assetPath, out var imported, out var indexValid)) return true;
        if (imported || !indexValid)
            FinishAssetImport(asset, assetPath, notify: false);
        return true;
    }

    Asset ApplyImportOverrides(Asset asset, string path)
    {
        if (PackageRootOf(path) is null || importOverrides.Get(asset.Id) is null) return asset;
        return Serializer.LoadData<Asset>(importOverrides.Apply(asset.Id, SerializeAssetMetadata(asset))) ?? asset;
    }

    void NotifyAssetsChanged()
    {
        AssetsChanged?.Invoke();
    }

    internal static string SerializeAssetMetadata(Asset asset)
    {
        return asset switch
        {
            Prefab prefab => Serializer.Serialize(prefab),
            DataAssetAsset dataAssetAsset => Serializer.Serialize(dataAssetAsset),
            ModelImportAsset modelImportAsset => Serializer.Serialize(modelImportAsset),
            ModelAssetMeta modelAssetMeta => Serializer.Serialize(modelAssetMeta),
            TextureAssetMeta textureAssetMeta => Serializer.Serialize(textureAssetMeta),
            SoundAssetMeta soundAssetMeta => Serializer.Serialize(soundAssetMeta),
            _ => Serializer.Serialize(asset)
        };
    }

    Asset CreateOrLoadAssetMetadata(string filePath, string metaFilePath)
    {
        if (File.Exists(metaFilePath))
        {
            try
            {
                var existing = Asset.Load(metaFilePath);
                if (existing is not null)
                {
                    existing = UpgradeUntypedMetadata(existing, filePath);
                    existing.RelativePath = filePath;
                    ApplyTypedDefaults(existing, filePath);
                    return existing;
                }
            }
            catch (UnresolvableTypeIdException)
            {
                throw;
            }
            catch
            {
                // ignored - fallback to re-creating metadata
            }
        }

        var asset = CreateAssetMetadata(filePath);
        asset.RelativePath = filePath;
        ApplyTypedDefaults(asset, filePath);
        return asset;
    }

    /// <summary>
    /// A meta written as a bare <see cref="Asset"/> — before its importer produced a typed one, or by a
    /// copy the fallback importer picked up — is recreated as the type its importer now makes, keeping
    /// its id so every reference to it still resolves.
    /// </summary>
    Asset UpgradeUntypedMetadata(Asset existing, string filePath)
    {
        if (existing.GetType() != typeof(Asset)) return existing;

        var typed = CreateAssetMetadata(filePath);
        if (typed.GetType() == typeof(Asset)) return existing;

        typed.Id = existing.Id;
        return typed;
    }

    Asset CreateAssetMetadata(string filePath)
    {
        foreach (var importer in assetImporters)
        {
            if (!importer.IsValid(filePath))
            {
                continue;
            }

            var asset = importer.CreateAsset(filePath);
            asset.RelativePath = filePath;
            ApplyTypedDefaults(asset, filePath);
            return asset;
        }

        var fallback = CreateDefaultAsset(filePath);
        ApplyTypedDefaults(fallback, filePath);
        return fallback;
    }

    bool TryImportAssetToCache(Asset asset, string sourceFilePath, out bool imported, out bool indexValid)
    {
        try
        {
            imported = ImportAssetToCache(asset, sourceFilePath, out indexValid);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogError(ex, "Failed to import asset {FilePath}: {Reason}", sourceFilePath, ex.Message);
            imported = false;
            indexValid = false;
            return false;
        }
    }

    bool ImportAssetToCache(Asset asset, string sourceFilePath, out bool indexValid)
    {
        indexValid = false;
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        if (cacheAssetsRootPath is null)
        {
            throw new InvalidOperationException("Asset cache paths were not initialized.");
        }

        var importDirectory = GetAssetImportDirectory(asset.Id);
        Directory.CreateDirectory(importDirectory);

        var settingsHash = ComputeStringHash(SerializeAssetMetadata(asset));
        var manifestPath = Path.Combine(importDirectory, importManifestFileName);

        var importer = assetImporters.FirstOrDefault(candidate => candidate.IsValid(sourceFilePath));
        var importerVersion = importer?.Version ?? 1;

        var existingManifest = LoadManifest(manifestPath);
        var source = new FileInfo(sourceFilePath);
        var sourceHash = SourceHash(existingManifest, source);
        if (CanReuseImport(existingManifest, sourceHash, settingsHash, importerVersion, importDirectory))
        {
            UpdateSourceStamp(existingManifest!, source, manifestPath);
            indexValid = HasCachedIndex(existingManifest!);
            return false;
        }

        ClearImportDirectory(importDirectory);
        Directory.CreateDirectory(importDirectory);

        var artifacts = importer is null
            ? IAssetImporter.CopySourceToCache(sourceFilePath, importDirectory)
            : importer.ImportToCache(asset, sourceFilePath, importDirectory);

        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Importer produced no cache artifact for '{sourceFilePath}'.");
        }

        var primaryArtifactOutputFileName = artifacts[0];
        var primaryArtifactPath = Path.Combine(importDirectory, primaryArtifactOutputFileName);

        var manifest = new ImportedAssetManifest
        {
            AssetId = asset.Id,
            SourceRelativePath = ToProjectRelativePath(sourceFilePath),
            MetaRelativePath = ToProjectRelativePath(GetMetaFilePath(sourceFilePath)),
            AssetTypeName = asset.GetType().FullName ?? nameof(Asset),
            ImporterId = ResolveImporterId(sourceFilePath),
            PipelineVersion = pipelineVersion,
            ImporterVersion = importerVersion,
            SourceHash = sourceHash,
            SourceLength = source.Length,
            SourceLastWriteTimeUtc = source.LastWriteTimeUtc,
            SettingsHash = settingsHash,
            PrimaryArtifactFileName = primaryArtifactOutputFileName,
            Artifacts = [.. artifacts],
            TargetArtifacts = MapTargetArtifacts(importer, artifacts),
            ImportedAtUtc = DateTimeOffset.UtcNow
        };

        Serializer.Save(manifestPath, manifest);

        logger.LogInformation(
            "Imported asset {AssetId} into cache: {PrimaryArtifactPath}",
            asset.Id,
            primaryArtifactPath);
        return true;
    }

}
