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
        if (ShouldIgnorePath(filePath) || IsMetaFilePath(filePath))
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            return;
        }

        var metaFilePath = GetMetaFilePath(filePath);

        if (!overwriteExisting && File.Exists(metaFilePath))
        {
            try
            {
                if (RegisterExistingMetaFile(metaFilePath))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to register asset meta file {MetaFilePath}. Overwriting malformed metadata",
                    metaFilePath);
            }
        }

        var asset = CreateOrLoadAssetMetadata(filePath, metaFilePath);
        var metaJson = SerializeAssetMetadata(asset);

        var metaDirectory = Path.GetDirectoryName(metaFilePath);
        if (!string.IsNullOrWhiteSpace(metaDirectory))
        {
            Directory.CreateDirectory(metaDirectory);
        }

        File.WriteAllText(metaFilePath, metaJson);

        ImportAssetToCache(asset, filePath);

        logger.LogInformation(
            "Asset metadata {Action}: {MetaFilePath}",
            overwriteExisting ? "updated" : "created",
            metaFilePath);

        FinishAssetImport(asset, filePath, notify: true);
    }

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
        if (!string.IsNullOrWhiteSpace(assetPath))
        {
            asset.RelativePath = assetPath;
        }

        if (!File.Exists(assetPath))
        {
            CleanupImportedArtifactsForAsset(asset);
            RebuildDatabase();
            return false;
        }

        ImportAssetToCache(asset, assetPath);

        FinishAssetImport(asset, assetPath, notify: false);
        return true;
    }

    void NotifyAssetsChanged()
    {
        AssetsChanged?.Invoke();
    }

    static string SerializeAssetMetadata(Asset asset)
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

    void ImportAssetToCache(Asset asset, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        if (cacheAssetsRootPath is null)
        {
            throw new InvalidOperationException("Asset cache paths were not initialized.");
        }

        var importDirectory = GetAssetImportDirectory(asset.Id);
        Directory.CreateDirectory(importDirectory);

        var sourceHash = ComputeFileHash(sourceFilePath);
        var settingsHash = ComputeStringHash(SerializeAssetMetadata(asset));
        var manifestPath = Path.Combine(importDirectory, importManifestFileName);

        var importer = assetImporters.FirstOrDefault(candidate => candidate.IsValid(sourceFilePath));
        var importerVersion = importer?.Version ?? 1;

        var existingManifest = LoadManifest(manifestPath);
        if (existingManifest is not null
            && existingManifest.SourceHash == sourceHash
            && existingManifest.SettingsHash == settingsHash
            && existingManifest.PipelineVersion == pipelineVersion
            && existingManifest.ImporterVersion == importerVersion
            && File.Exists(Path.Combine(importDirectory, existingManifest.PrimaryArtifactFileName)))
        {
            return;
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
    }

}
