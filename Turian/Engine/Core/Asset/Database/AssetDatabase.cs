namespace Turian.Engine.Core;

/// <summary>
/// Represents the central asset catalog used by the editor and runtime.
/// It can resolve imported loose files from the project cache and packed content from exported builds.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed partial class AssetDatabase
{
    const string cacheDirectoryName = ".Cache";
    const string cacheCatalogFileName = "assetCatalog.json";
    const string runtimeContentDirectoryName = "Content";
    const string runtimeCatalogFileName = "assetCatalog.json";

    readonly object syncRoot = new();

    static AssetDatabase? instance;

    /// <summary>
    /// Gets the singleton instance of the <see cref="AssetDatabase"/>.
    /// </summary>
    public static AssetDatabase Instance =>
        instance ?? throw new InvalidOperationException("AssetDatabase not initialized");

    /// <summary>
    /// Gets the singleton instance if one has been created, without throwing. For callers that
    /// run both inside a loaded project and standalone (the UI compositor, driven by code-built
    /// panels in headless demos and by document assets in a project).
    /// </summary>
    /// <param name="database">The instance, or <c>null</c> when no project is loaded.</param>
    /// <returns><c>true</c> when an instance exists.</returns>
    public static bool TryGetInstance(out AssetDatabase? database)
    {
        database = instance;
        return database is not null;
    }

    /// <summary>
    /// Gets the indexed asset records keyed by asset identifier.
    /// </summary>
    public Dictionary<Guid, AssetRecord> Assets { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetDatabase"/> class.
    /// </summary>
    public AssetDatabase()
    {
        instance = instance is null
            ? this
            : throw new InvalidOperationException("AssetDatabase already initialized");
    }

    /// <summary>
    /// Builds the database for a project by scanning source metadata under the project's <c>Assets</c> folder
    /// and merging it with the persisted cache catalog when available.
    /// </summary>
    /// <param name="assetFolderPath">The absolute path to the project's <c>Assets</c> directory.</param>
    /// <param name="recursive">Whether metadata files should be discovered recursively.</param>
    public void BuildDatabase(string assetFolderPath, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetFolderPath);

        var normalizedAssetsRoot = Path.GetFullPath(assetFolderPath);
        if (!Directory.Exists(normalizedAssetsRoot))
        {
            lock (syncRoot)
            {
                Assets.Clear();
            }

            return;
        }

        var projectRoot = Directory.GetParent(normalizedAssetsRoot)?.FullName
            ?? throw new InvalidOperationException("Unable to determine project root from asset folder path.");

        var rebuiltAssets = BuildProjectAssetMap(projectRoot, normalizedAssetsRoot, recursive);

        lock (syncRoot)
        {
            Assets = rebuiltAssets;
        }
    }

    /// <summary>
    /// Loads the asset catalog stored in the project's cache and replaces the current database contents.
    /// </summary>
    /// <param name="projectRootPath">The absolute project root path.</param>
    /// <returns>
    /// Whether the catalog was read, was absent, or was present but unusable. Callers that open a
    /// project for a user should act on <see cref="AssetCatalogLoadStatus.Unreadable"/> rather than
    /// carry on with an empty database, which presents as every scene opening empty.
    /// </returns>
    public AssetCatalogLoadStatus LoadCatalogFromProject(string projectRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        var projectRoot = Path.GetFullPath(projectRootPath);
        var catalogPath = GetProjectCatalogPath(projectRoot);
        var catalog = ReadCatalog(catalogPath, out var status);

        var loadedAssets = NormalizeCatalogRecords(projectRoot, catalog.Records);

        lock (syncRoot)
        {
            Assets = loadedAssets;
        }

        return status;
    }

    /// <summary>
    /// Loads the runtime catalog from an exported game output and replaces the current database contents.
    /// </summary>
    /// <param name="runtimeRootPath">The absolute exported runtime root path.</param>
    public void LoadRuntimeCatalog(string runtimeRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootPath);

        var runtimeRoot = Path.GetFullPath(runtimeRootPath);
        var catalogPath = Path.Combine(runtimeRoot, runtimeContentDirectoryName, runtimeCatalogFileName);
        var catalog = ReadCatalog(catalogPath);

        var loadedAssets = NormalizeCatalogRecords(runtimeRoot, catalog.Records);

        lock (syncRoot)
        {
            Assets = loadedAssets;
        }
    }

    /// <summary>
    /// Saves the current database records as a simple dictionary JSON file.
    /// </summary>
    /// <param name="path">The target file path.</param>
    public void SaveDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Dictionary<Guid, AssetRecord> snapshot;
        lock (syncRoot)
        {
            snapshot = Assets.ToDictionary(static kvp => kvp.Key, static kvp => CloneRecord(kvp.Value));
        }

        EnsureParentDirectory(path);
        Serializer.Save(path, snapshot);
    }

    /// <summary>
    /// Loads the database from a simple dictionary JSON file.
    /// </summary>
    /// <param name="path">The source file path.</param>
    public void LoadDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return;
        }

        var loadedAssets = Serializer.Load<Dictionary<Guid, AssetRecord>>(path) ?? [];

        foreach (var record in loadedAssets.Values)
        {
            NormalizeRecord(record.ProjectRootPath, record);
        }

        lock (syncRoot)
        {
            Assets = loadedAssets.ToDictionary(static kvp => kvp.Key, static kvp => CloneRecord(kvp.Value));
        }
    }

    /// <summary>
    /// Saves the current asset catalog into the project's cache.
    /// </summary>
    /// <param name="projectRootPath">The absolute project root path.</param>
    public void SaveCatalog(string projectRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        AssetCatalog catalog;
        lock (syncRoot)
        {
            catalog = new AssetCatalog
            {
                Version = AssetCatalog.CurrentVersion,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Records = [.. Assets.Values.Select(CloneRecord)]
            };
        }

        var projectRoot = Path.GetFullPath(projectRootPath);
        var catalogPath = GetProjectCatalogPath(projectRoot);
        SaveCatalog(catalog, catalogPath);
    }

    /// <summary>
    /// Registers or updates an asset entry using the provided metadata.
    /// </summary>
    /// <param name="asset">The asset metadata.</param>
    /// <param name="importedPrimaryContentPath">Optional absolute imported payload path.</param>
    /// <returns><c>true</c> if the asset was registered; otherwise, <c>false</c>.</returns>
    public bool RegisterAsset(Asset? asset, string? importedPrimaryContentPath = null)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.RelativePath))
        {
            return false;
        }

        var sourcePath = Path.GetFullPath(asset.RelativePath);
        var projectRoot = TryResolveProjectRoot(sourcePath);
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }

        var metaPath = $"{sourcePath}.meta";
        var record = CreateAssetRecord(asset, projectRoot, sourcePath, metaPath, importedPrimaryContentPath);

        lock (syncRoot)
        {
            Assets[record.AssetId] = record;
        }

        return true;
    }

    /// <summary>
    /// Registers or updates a child asset — a material or texture declared inside a model file
    /// rather than by a source file of its own. The child has no <c>.meta</c>; it is addressed
    /// through its parent, and its payload lives in the parent's import directory.
    /// </summary>
    /// <param name="parentAssetId">Identifier of the asset the child was imported from.</param>
    /// <param name="child">The child asset metadata.</param>
    /// <param name="importedContentPath">Absolute path of the child's serialized payload.</param>
    /// <returns><c>true</c> when the child was registered; otherwise <c>false</c>.</returns>
    public bool RegisterChildAsset(Guid parentAssetId, Asset? child, string? importedContentPath = null)
    {
        if (child is null || parentAssetId == Guid.Empty || child.Id == Guid.Empty)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (!Assets.TryGetValue(parentAssetId, out var parent))
            {
                return false;
            }

            var record = new AssetRecord
            {
                AssetId = child.Id,
                ParentAssetId = parentAssetId,
                ProjectRootPath = parent.ProjectRootPath,
                AssetTypeName = child.GetType().FullName ?? nameof(Asset),
                // A child has no source file of its own: point at the parent's so the editor can
                // still answer "where did this come from?" without inventing a path that is not there.
                SourceRelativePath = parent.SourceRelativePath,
                MetaRelativePath = parent.MetaRelativePath,
                PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(child.Id),
                ImportedRelativePath = string.IsNullOrWhiteSpace(importedContentPath)
                    ? string.Empty
                    : TryMakeRelativeProjectPath(parent.ProjectRootPath, Path.GetFullPath(importedContentPath)),
                ImporterId = parent.ImporterId,
                ImporterVersion = parent.ImporterVersion
            };

            record.StorageKind = string.IsNullOrWhiteSpace(record.ImportedRelativePath)
                ? AssetStorageKind.Unknown
                : GuessStorageKind(record.ImportedRelativePath);

            if (!string.IsNullOrWhiteSpace(record.ImportedRelativePath))
            {
                record.Artifacts.Add(record.ImportedRelativePath);
            }

            Assets[record.AssetId] = record;
        }

        return true;
    }

    /// <summary>
    /// Removes every child asset of <paramref name="parentAssetId"/> except those in
    /// <paramref name="keep"/>. Called after a reimport so slots that no longer exist in the
    /// source file — a material the artist deleted — do not linger in the catalog.
    /// </summary>
    /// <param name="parentAssetId">Identifier of the parent asset.</param>
    /// <param name="keep">Child identifiers that are still valid. Pass an empty set to remove all.</param>
    /// <returns>The number of child records removed.</returns>
    public int RemoveChildAssets(Guid parentAssetId, IReadOnlySet<Guid>? keep = null)
    {
        if (parentAssetId == Guid.Empty)
        {
            return 0;
        }

        lock (syncRoot)
        {
            var stale = Assets.Values
                .Where(record => record.ParentAssetId == parentAssetId
                                 && (keep is null || !keep.Contains(record.AssetId)))
                .Select(static record => record.AssetId)
                .ToList();

            foreach (var assetId in stale)
            {
                Assets.Remove(assetId);
            }

            return stale.Count;
        }
    }

    /// <summary>
    /// Gets the child assets registered against <paramref name="parentAssetId"/>.
    /// </summary>
    /// <param name="parentAssetId">Identifier of the parent asset.</param>
    /// <returns>A snapshot of the parent's child records.</returns>
    public IReadOnlyCollection<AssetRecord> GetChildAssets(Guid parentAssetId)
    {
        lock (syncRoot)
        {
            return Assets.Values
                .Where(record => record.ParentAssetId == parentAssetId)
                .Select(CloneRecord)
                .ToList();
        }
    }

}
