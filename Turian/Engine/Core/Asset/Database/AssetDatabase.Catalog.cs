namespace Turian.Engine.Core;

public sealed partial class AssetDatabase
{
    static Dictionary<Guid, AssetRecord> BuildProjectAssetMap(
        string projectRoot,
        string assetsRoot,
        bool recursive)
    {
        var rebuiltAssets = new Dictionary<Guid, AssetRecord>();
        var cacheCatalogPath = GetProjectCatalogPath(projectRoot);
        var catalog = ReadCatalog(cacheCatalogPath);

        foreach (var record in catalog.Records)
        {
            if (record.AssetId == Guid.Empty)
            {
                continue;
            }

            NormalizeRecord(projectRoot, record);
            rebuiltAssets[record.AssetId] = CloneRecord(record);
        }

        var importedAssetsRoot = Path.Combine(projectRoot, cacheDirectoryName, "Assets");
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var metaFiles = Directory.GetFiles(assetsRoot, "*.meta", searchOption);

        foreach (var metaFilePath in metaFiles)
        {
            var record = TryBuildAssetRecord(projectRoot, importedAssetsRoot, metaFilePath, rebuiltAssets);
            if (record is null)
            {
                continue;
            }

            rebuiltAssets[record.AssetId] = record;
        }

        DropRecordsWithoutMetaFile(rebuiltAssets);
        DropOrphanedChildren(rebuiltAssets);

        return rebuiltAssets;
    }

    /// <summary>
    /// Drops catalog-seeded records whose meta file is gone from disk. This is the editor's
    /// "rescan the project" path and the meta file is the authority on whether a loose asset
    /// still exists, so a record the catalog remembers but the folder no longer has is stale.
    /// Records that carry no meta path are left alone — there is nothing to check them against.
    /// </summary>
    static void DropRecordsWithoutMetaFile(Dictionary<Guid, AssetRecord> assets)
    {
        var stale = assets.Values
            .Where(static record => !string.IsNullOrWhiteSpace(record.MetaRelativePath))
            .Where(record => !File.Exists(ResolveRecordPath(record.ProjectRootPath, record.MetaRelativePath)))
            .Select(static record => record.AssetId)
            .ToList();

        foreach (var assetId in stale)
        {
            assets.Remove(assetId);
        }
    }

    static string ResolveRecordPath(string? projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        return string.IsNullOrWhiteSpace(projectRoot)
            ? relativePath
            : Path.Combine(projectRoot, relativePath);
    }

    /// <summary>
    /// Removes child records whose parent is no longer in the database. Child assets are seeded
    /// from the cache catalog rather than from <c>.meta</c> files, so deleting a model file would
    /// otherwise leave its materials and textures behind forever.
    /// </summary>
    static void DropOrphanedChildren(Dictionary<Guid, AssetRecord> assets)
    {
        var orphans = assets.Values
            .Where(record => record.ParentAssetId != Guid.Empty
                             && !assets.ContainsKey(record.ParentAssetId))
            .Select(static record => record.AssetId)
            .ToList();

        foreach (var orphan in orphans)
        {
            assets.Remove(orphan);
        }
    }

    static AssetRecord? TryBuildAssetRecord(
        string projectRoot,
        string importedAssetsRoot,
        string metaFilePath,
        IReadOnlyDictionary<Guid, AssetRecord> existingRecords)
    {
        if (string.IsNullOrWhiteSpace(metaFilePath) || !File.Exists(metaFilePath))
        {
            return null;
        }

        var asset = TryLoadAssetMetadata(metaFilePath);
        if (asset is null)
        {
            return null;
        }

        var sourcePath = Path.GetFullPath(GetAssetPathFromMeta(metaFilePath));
        existingRecords.TryGetValue(asset.Id, out var existingRecord);

        var record = CreateAssetRecord(
            asset,
            projectRoot,
            sourcePath,
            metaFilePath,
            importedPrimaryContentPath: null,
            importedAssetsRoot);

        if (existingRecord is not null)
        {
            record.SourceHash = string.IsNullOrWhiteSpace(existingRecord.SourceHash)
                ? record.SourceHash
                : existingRecord.SourceHash;
            record.SettingsHash = string.IsNullOrWhiteSpace(existingRecord.SettingsHash)
                ? record.SettingsHash
                : existingRecord.SettingsHash;
            record.ImporterId = string.IsNullOrWhiteSpace(existingRecord.ImporterId)
                ? record.ImporterId
                : existingRecord.ImporterId;
            record.ImporterVersion = existingRecord.ImporterVersion == 0
                ? record.ImporterVersion
                : existingRecord.ImporterVersion;
            if (existingRecord.ComponentTypes is { Count: > 0 })
            {
                record.ComponentTypes = [.. existingRecord.ComponentTypes];
            }
        }

        return record;
    }

    static AssetRecord CreateAssetRecord(
        Asset asset,
        string projectRoot,
        string sourcePath,
        string metaPath,
        string? importedPrimaryContentPath = null,
        string? importedAssetsRoot = null)
    {
        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        var normalizedMetaPath = Path.GetFullPath(metaPath);

        var fallbackImportedRelativePath = ResolveImportedRelativePathFromCache(asset.Id, importedAssetsRoot);
        var importedRelativePath = ResolveImportedRelativePath(
            projectRoot,
            normalizedSourcePath,
            importedPrimaryContentPath,
            fallbackImportedRelativePath);

        return new AssetRecord
        {
            AssetId = asset.Id,
            ProjectRootPath = projectRoot,
            AssetTypeName = asset.GetType().FullName ?? nameof(Asset),
            Labels = [.. asset.Labels],
            SourceRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedSourcePath),
            MetaRelativePath = TryMakeRelativeProjectPath(projectRoot, normalizedMetaPath),
            PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(asset.Id),
            ImportedRelativePath = importedRelativePath,
            StorageKind = string.IsNullOrWhiteSpace(importedRelativePath)
                ? AssetStorageKind.Unknown
                : GuessStorageKind(importedRelativePath),
            ImporterId = string.Empty,
            ImporterVersion = 0,
            SourceHash = string.Empty,
            SettingsHash = string.Empty,
            Artifacts = ResolveArtifacts(
                projectRoot,
                normalizedSourcePath,
                importedPrimaryContentPath,
                fallbackImportedRelativePath),
            TargetArtifacts = ResolveTargetArtifacts(projectRoot, asset.Id, importedAssetsRoot)
        };
    }

    static Dictionary<Guid, AssetRecord> NormalizeCatalogRecords(
        string projectRoot,
        IEnumerable<AssetRecord> records)
    {
        var loaded = new Dictionary<Guid, AssetRecord>();

        foreach (var record in records)
        {
            if (record.AssetId == Guid.Empty)
            {
                continue;
            }

            NormalizeRecord(projectRoot, record);
            loaded[record.AssetId] = CloneRecord(record);
        }

        return loaded;
    }

    static void NormalizeRecord(string? projectRoot, AssetRecord record)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            record.ProjectRootPath = projectRoot;
        }

        if (record.AssetId != Guid.Empty && string.IsNullOrWhiteSpace(record.PrimaryContentKey))
        {
            record.PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(record.AssetId);
        }

        if (record.StorageKind == AssetStorageKind.Unknown)
        {
            record.StorageKind = GuessStorageKind(record);
        }

        if (record.Artifacts.Count == 0 && !string.IsNullOrWhiteSpace(record.ImportedRelativePath))
        {
            record.Artifacts.Add(record.ImportedRelativePath);
        }
    }

    static AssetStorageKind GuessStorageKind(AssetRecord record)
    {
        return GuessStorageKind(record.ImportedRelativePath);
    }

    static AssetStorageKind GuessStorageKind(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return AssetStorageKind.Unknown;
        }

        return relativePath.EndsWith(OapFormat.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? AssetStorageKind.Oap
            : AssetStorageKind.LooseFile;
    }
}
