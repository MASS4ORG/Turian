namespace Turian.Engine.Core;

public sealed partial class AssetDatabase
{
    /// <summary>
    /// Registers or updates an asset entry using a meta file path.
    /// </summary>
    /// <param name="metaFilePath">The absolute meta file path.</param>
    /// <param name="importedPrimaryContentPath">Optional absolute imported payload path.</param>
    /// <returns><c>true</c> if the asset was registered; otherwise, <c>false</c>.</returns>
    public bool RegisterAssetFromMetaFile(string metaFilePath, string? importedPrimaryContentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaFilePath);

        var asset = TryLoadAssetMetadata(metaFilePath);
        if (asset is null)
        {
            return false;
        }

        asset.RelativePath = GetAssetPathFromMeta(metaFilePath);
        return RegisterAsset(asset, importedPrimaryContentPath);
    }

    /// <summary>
    /// Removes an asset from the database by its identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <returns><c>true</c> if the asset existed and was removed; otherwise, <c>false</c>.</returns>
    public bool RemoveAsset(Guid assetId)
    {
        lock (syncRoot)
        {
            return Assets.Remove(assetId);
        }
    }

    /// <summary>
    /// Removes an asset from the database using its metadata instance.
    /// </summary>
    /// <param name="asset">The asset metadata.</param>
    /// <returns><c>true</c> if the asset existed and was removed; otherwise, <c>false</c>.</returns>
    public bool RemoveAsset(Asset? asset)
    {
        return asset is not null && RemoveAsset(asset.Id);
    }

    /// <summary>
    /// Removes an asset from the database using a meta file path.
    /// </summary>
    /// <param name="metaFilePath">The absolute meta file path.</param>
    /// <returns><c>true</c> if the asset existed and was removed; otherwise, <c>false</c>.</returns>
    public bool RemoveAssetFromMetaFile(string metaFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaFilePath);

        var asset = TryLoadAssetMetadata(metaFilePath);
        return RemoveAsset(asset);
    }

    /// <summary>
    /// Updates an asset's source path and optional imported content path.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="newSourcePath">The new absolute source path.</param>
    /// <param name="newImportedPrimaryContentPath">Optional absolute imported payload path.</param>
    public void MoveAsset(Guid assetId, string newSourcePath, string? newImportedPrimaryContentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);

        var normalizedSourcePath = Path.GetFullPath(newSourcePath);
        var projectRoot = TryResolveProjectRoot(normalizedSourcePath)
            ?? throw new InvalidOperationException("Unable to determine project root for asset move.");

        var normalizedMetaPath = $"{normalizedSourcePath}.meta";

        lock (syncRoot)
        {
            if (!Assets.TryGetValue(assetId, out var existingRecord))
            {
                Assets[assetId] = new AssetRecord
                {
                    AssetId = assetId,
                    ProjectRootPath = projectRoot,
                    AssetTypeName = typeof(Asset).FullName ?? nameof(Asset),
                    SourceRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedSourcePath),
                    MetaRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedMetaPath),
                    PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(assetId),
                    ImportedRelativePath = ResolveImportedRelativePath(
                        projectRoot,
                        normalizedSourcePath,
                        newImportedPrimaryContentPath),
                    StorageKind = AssetStorageKind.LooseFile,
                    Artifacts = ResolveArtifacts(
                        projectRoot,
                        normalizedSourcePath,
                        newImportedPrimaryContentPath,
                        fallbackImportedRelativePath: null)
                };

                return;
            }

            existingRecord.ProjectRootPath = projectRoot;
            existingRecord.SourceRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedSourcePath);
            existingRecord.MetaRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedMetaPath);
            existingRecord.ImportedRelativePath = ResolveImportedRelativePath(
                projectRoot,
                normalizedSourcePath,
                newImportedPrimaryContentPath,
                existingRecord.ImportedRelativePath);
            existingRecord.StorageKind = GuessStorageKind(existingRecord);
            existingRecord.Artifacts = ResolveArtifacts(
                projectRoot,
                normalizedSourcePath,
                newImportedPrimaryContentPath,
                existingRecord.ImportedRelativePath);
        }
    }

    /// <summary>
    /// Updates an asset's path information using its metadata instance.
    /// </summary>
    /// <param name="asset">The asset metadata.</param>
    /// <param name="importedPrimaryContentPath">Optional absolute imported payload path.</param>
    /// <returns><c>true</c> if the asset was updated; otherwise, <c>false</c>.</returns>
    public bool MoveAsset(Asset? asset, string? importedPrimaryContentPath = null)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.RelativePath))
        {
            return false;
        }

        MoveAsset(asset.Id, asset.RelativePath, importedPrimaryContentPath);
        return true;
    }

    /// <summary>
    /// Updates an asset's path information using a meta file path.
    /// </summary>
    /// <param name="metaFilePath">The absolute meta file path.</param>
    /// <param name="importedPrimaryContentPath">Optional absolute imported payload path.</param>
    /// <returns><c>true</c> if the asset was updated; otherwise, <c>false</c>.</returns>
    public bool MoveAssetFromMetaFile(string metaFilePath, string? importedPrimaryContentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaFilePath);

        var asset = TryLoadAssetMetadata(metaFilePath);
        if (asset is null)
        {
            return false;
        }

        asset.RelativePath = GetAssetPathFromMeta(metaFilePath);
        return MoveAsset(asset, importedPrimaryContentPath);
    }

    /// <summary>
    /// Attempts to get a registered asset record.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="record">The resolved record.</param>
    /// <returns><c>true</c> if the asset exists; otherwise, <c>false</c>.</returns>
    public bool TryGetAsset(Guid assetId, out AssetRecord? record)
    {
        lock (syncRoot)
        {
            if (Assets.TryGetValue(assetId, out var existingRecord))
            {
                record = CloneRecord(existingRecord);
                return true;
            }
        }

        record = null;
        return false;
    }

    /// <summary>
    /// Attempts to get a file provider for the asset's primary content.
    /// Supports both loose imported files and packed runtime content.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="provider">The resolved provider.</param>
    /// <returns><c>true</c> if the provider could be created; otherwise, <c>false</c>.</returns>
    public bool TryGetAssetProvider(Guid assetId, out IAssetFileProvider? provider)
    {
        lock (syncRoot)
        {
            if (!Assets.TryGetValue(assetId, out var record))
            {
                provider = null;
                return false;
            }

            provider = CreateProvider(record);
            return provider is not null;
        }
    }

    /// <summary>
    /// Gets a snapshot of all indexed asset records.
    /// </summary>
    /// <returns>A read-only snapshot of asset records.</returns>
    public IReadOnlyCollection<AssetRecord> GetAssetsSnapshot()
    {
        lock (syncRoot)
        {
            return Assets.Values
                .Select(CloneRecord)
                .ToArray();
        }
    }

    /// <summary>
    /// Updates the cached list of component type names carried by the asset's content.
    /// Only meaningful for prefab-like assets; callers pass the fully-qualified names of
    /// every <c>Component</c> instance found in the prefab hierarchy.
    /// </summary>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="componentTypes">The component type names to store.</param>
    /// <returns><c>true</c> if the record was updated; otherwise <c>false</c>.</returns>
    public bool UpdateComponentTypes(Guid assetId, IReadOnlyCollection<string> componentTypes)
    {
        ArgumentNullException.ThrowIfNull(componentTypes);

        lock (syncRoot)
        {
            if (!Assets.TryGetValue(assetId, out var record))
            {
                return false;
            }

            record.ComponentTypes = [.. componentTypes];
            return true;
        }
    }

    /// <summary>
    /// Merges the provided assets into the database.
    /// </summary>
    /// <param name="assetsNew">The asset records to merge.</param>
    public void MergeDatabase(Dictionary<Guid, AssetRecord>? assetsNew)
    {
        if (assetsNew is null)
        {
            return;
        }

        lock (syncRoot)
        {
            foreach (var kvp in assetsNew)
            {
                Assets[kvp.Key] = CloneRecord(kvp.Value);
            }
        }
    }

}
