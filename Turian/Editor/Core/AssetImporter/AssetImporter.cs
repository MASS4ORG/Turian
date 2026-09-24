namespace Turian.Editor.Core;

/// <summary>
/// Scans the project's asset folder, generates meta files, imports source assets into the cache,
/// keeps the asset database in sync, monitors file changes, and publishes change notifications
/// for Studio panels.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed partial class AssetImporter : IDisposable
{
    static readonly string[] ignoredExtensions =
    [
        ".cs",   // source code lives in Assets/ but is compiled, not imported as an asset
        ".tmp",
        ".temp",
        ".bak"
    ];

    const string cacheDirectoryName = ".Cache";
    const string cacheAssetsDirectoryName = "Assets";
    const string cacheByGuidDirectoryName = "by-guid";
    const string importManifestFileName = "import.json";
    const string childAssetsDirectoryName = "children";
    const int pipelineVersion = 2;
    const int importProgressInterval = 25;

    readonly ILogger logger;
    readonly AssetDatabase assetDatabase;
    readonly SettingsService settingsService;
    readonly List<IAssetImporter> assetImporters;
    readonly object syncRoot = new();

    AssetFolderWatcher? folderWatcher;
    List<(Asset Asset, string SourcePath)>? pendingBatch;
    string? projectRootPath;
    string? assetsRootPath;
    string? cacheRootPath;
    string? cacheAssetsRootPath;
    bool disposed;

    /// <summary>
    /// Raised after the asset folder content has changed and the importer has refreshed its state.
    /// </summary>
    public event Action? AssetsChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetImporter"/> class.
    /// </summary>
    public AssetImporter(
        ILogger logger,
        AssetDatabase assetDatabase,
        SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(settingsService);

        this.logger = logger;
        this.assetDatabase = assetDatabase;
        this.settingsService = settingsService;

        assetImporters = BuildImporterList();

        if (this.settingsService is { HasSettings: true, Settings: not null })
        {
            InitializeForAssetsRoot(this.settingsService.Settings.AssetsAbsoluteDir);
        }
    }

    /// <summary>
    /// Generates missing meta files, imports all assets into the cache,
    /// and rebuilds the asset database.
    /// </summary>
    public void GenerateMetaFiles(string assetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(assetFolderPath) || !Directory.Exists(assetFolderPath))
        {
            logger.LogWarning("The specified asset folder does not exist: {AssetFolderPath}", assetFolderPath);
            return;
        }

        lock (syncRoot)
        {
            InitializePaths(assetFolderPath);
            ScanFolderBatched(assetFolderPath);
        }

        NotifyAssetsChanged();
    }

    /// <summary>
    /// Runs <see cref="GenerateMetaFiles"/> on a background thread so a large project does not
    /// block the caller. Progress is reported through the logger.
    /// </summary>
    /// <param name="assetFolderPath">The absolute path of the project's asset folder.</param>
    public Task GenerateMetaFilesAsync(string assetFolderPath) =>
        Task.Run(() => GenerateMetaFiles(assetFolderPath));

    /// <summary>
    /// Starts monitoring the currently loaded project's asset folder.
    /// </summary>
    public void StartMonitoring()
    {
        if (settingsService.Settings is null)
        {
            logger.LogWarning("Asset monitoring requested before project settings were loaded");
            return;
        }

        InitializeForAssetsRoot(settingsService.Settings.AssetsAbsoluteDir);
    }

    /// <summary>
    /// Runs <see cref="StartMonitoring"/> on a background thread so opening a project does not
    /// block the caller while its assets are imported. Progress is reported through the logger.
    /// </summary>
    public Task StartMonitoringAsync() => Task.Run(StartMonitoring);

    /// <summary>
    /// Stops monitoring the asset folder.
    /// </summary>
    public void StopMonitoring()
    {
        lock (syncRoot)
        {
            folderWatcher?.Stop();
        }
    }

    /// <summary>
    /// The importer that owns a source file, which is the one whose import settings the inspector
    /// draws. Null when nothing claims it, such as a file the scan ignores.
    /// </summary>
    /// <param name="filePath">Absolute path of the source file.</param>
    /// <returns>The importer, or null.</returns>
    public IAssetImporter? ImporterFor(string filePath) =>
        string.IsNullOrWhiteSpace(filePath) || ShouldIgnorePath(filePath)
            ? null
            : assetImporters.FirstOrDefault(candidate => candidate.IsValid(filePath));

    List<IAssetImporter> BuildImporterList()
    {
        return [.. BuildManager.Instance.LoadedAssemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type =>
                typeof(IAssetImporter).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false })
            .Select(type => new
            {
                Type = type,
                IsLastResort = type.GetCustomAttributes(typeof(DefaultOptionAttribute), false).Length > 0
            })
            .OrderBy(static item => item.IsLastResort)
            .Select(static item => Activator.CreateInstance(item.Type) as IAssetImporter)
            .Where(static importer => importer is not null)
            .Cast<IAssetImporter>()];
    }

    void InitializeForAssetsRoot(string assetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(assetFolderPath))
        {
            return;
        }

        if (!Directory.Exists(assetFolderPath))
        {
            logger.LogWarning(
                "Unable to initialize asset importer. Asset folder does not exist: {AssetFolderPath}",
                assetFolderPath);
            return;
        }

        lock (syncRoot)
        {
            InitializePaths(assetFolderPath);

            ScanFolderBatched(assetsRootPath!);

            folderWatcher?.Dispose();
            folderWatcher = new AssetFolderWatcher(logger, this);
            folderWatcher.Start(assetsRootPath!);
        }

        NotifyAssetsChanged();
    }

    void InitializePaths(string assetFolderPath)
    {
        assetsRootPath = Path.GetFullPath(assetFolderPath);
        projectRootPath = Directory.GetParent(assetsRootPath)?.FullName
            ?? throw new InvalidOperationException("Unable to determine project root from Assets path.");
        cacheRootPath = Path.Combine(projectRootPath, cacheDirectoryName);
        cacheAssetsRootPath = Path.Combine(cacheRootPath, cacheAssetsDirectoryName);
        Directory.CreateDirectory(cacheAssetsRootPath);
    }

    /// <summary>
    /// Imports every file under <paramref name="folderPath"/>, then rebuilds the database,
    /// registers the child assets and persists the catalog once for the whole scan.
    /// </summary>
    void ScanFolderBatched(string folderPath)
    {
        var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
        var batch = new List<(Asset Asset, string SourcePath)>();

        logger.LogInformation("Importing {FileCount} files from {FolderPath}", files.Length, folderPath);

        pendingBatch = batch;
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                EnsureAssetImported(files[i], overwriteExisting: false);

                if ((i + 1) % importProgressInterval == 0 || i + 1 == files.Length)
                {
                    logger.LogInformation("Asset import: {Imported}/{FileCount} files", i + 1, files.Length);
                }
            }

            RebuildDatabase();

            // The batch stays open here: a model reconfiguring its textures reimports them, and an
            // immediate rebuild would reseed the database from the on-disk catalog, discarding the
            // children registered so far. Those reimports append to the batch and are handled below.
            for (var i = 0; i < batch.Count; i++)
            {
                var (asset, sourcePath) = batch[i];
                RegisterChildAssets(asset, sourcePath);
                RefreshPrefabComponentIndex(asset, ResolveImportedPrimaryPath(asset.Id));
            }
        }
        finally
        {
            pendingBatch = null;
        }

        PersistCacheCatalog();

        logger.LogInformation("Asset import finished: {ImportedCount} assets from {FolderPath}", batch.Count, folderPath);
        ReportTextureCacheSize();
    }

    /// <summary>
    /// Logs how much cache the baked texture artifacts occupy. This is the on-disk figure, which
    /// is also what the GPU uploads: the artifacts hold exactly the blocks it samples, already
    /// capped by <see cref="TextureImportSettings.MaxResolution"/>.
    /// </summary>
    void ReportTextureCacheSize()
    {
        long bytes = 0;
        var count = 0;

        foreach (var record in assetDatabase.Assets.Values)
        {
            if (record.AssetTypeName != typeof(TextureAsset).FullName) continue;

            foreach (var artifact in record.TargetArtifacts.Values)
            {
                var path = Path.Combine(record.ProjectRootPath, artifact);
                if (!File.Exists(path)) continue;

                bytes += new FileInfo(path).Length;
                count++;
            }
        }

        if (count == 0) return;

        logger.LogInformation(
            "Texture cache: {Megabytes:F1} MiB across {Count} baked textures",
            bytes / (1024.0 * 1024.0),
            count);
    }

    void OnWatcherCreated(FileSystemEventArgs e)
    {
        lock (syncRoot)
        {
            if (ShouldIgnorePath(e.FullPath))
            {
                return;
            }

            if (Directory.Exists(e.FullPath))
            {
                ScanFolderBatched(e.FullPath);
                NotifyAssetsChanged();
                return;
            }

            if (IsMetaFilePath(e.FullPath))
            {
                if (File.Exists(e.FullPath))
                {
                    RegisterExistingMetaFile(e.FullPath);
                    NotifyAssetsChanged();
                }

                return;
            }

            EnsureAssetImported(e.FullPath, overwriteExisting: false);
        }
    }

    void OnWatcherChanged(FileSystemEventArgs e)
    {
        lock (syncRoot)
        {
            if (ShouldIgnorePath(e.FullPath))
            {
                return;
            }

            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            if (IsMetaFilePath(e.FullPath))
            {
                if (File.Exists(e.FullPath))
                {
                    RegisterExistingMetaFile(e.FullPath);
                    NotifyAssetsChanged();
                }

                return;
            }

            EnsureAssetImported(e.FullPath, overwriteExisting: true);
        }
    }

    void OnWatcherDeleted(FileSystemEventArgs e)
    {
        lock (syncRoot)
        {
            if (IsMetaFilePath(e.FullPath))
            {
                if (ShouldIgnoreMetaPath(e.FullPath))
                {
                    return;
                }

                HandleDeletedMeta(e.FullPath);
                return;
            }

            if (ShouldIgnorePath(e.FullPath))
            {
                return;
            }

            HandleDeletedAsset(e.FullPath);
        }
    }

    void OnWatcherRenamed(RenamedEventArgs e)
    {
        lock (syncRoot)
        {
            if (!ShouldHandleRename(e.OldFullPath, e.FullPath))
            {
                return;
            }

            HandleRenamedPath(e.OldFullPath, e.FullPath);
        }

        NotifyAssetsChanged();
    }

    bool ShouldHandleRename(string oldPath, string newPath)
    {
        var oldUnderRoot = IsUnderAssetsRoot(oldPath);
        var newUnderRoot = IsUnderAssetsRoot(newPath);

        return oldUnderRoot || newUnderRoot;
    }

    void HandleRenamedPath(string oldPath, string newPath)
    {
        if (Directory.Exists(newPath))
        {
            ScanFolderBatched(newPath);
            return;
        }

        if (IsMetaFilePath(oldPath) || IsMetaFilePath(newPath))
        {
            RebuildDatabase();
            return;
        }

        var oldMetaPath = GetMetaFilePath(oldPath);
        var newMetaPath = GetMetaFilePath(newPath);

        if (File.Exists(oldMetaPath) && !File.Exists(newMetaPath))
        {
            var destinationDirectory = Path.GetDirectoryName(newMetaPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Move(oldMetaPath, newMetaPath);
        }

        if (File.Exists(newPath))
        {
            EnsureAssetImported(newPath, overwriteExisting: true);
        }

        if (File.Exists(oldPath) && !File.Exists(newPath))
        {
            CleanupImportedArtifactsForAssetPath(oldPath);
        }

        RebuildDatabase();
    }

    void HandleDeletedAsset(string assetPath)
    {
        var metaPath = GetMetaFilePath(assetPath);

        CleanupImportedArtifactsForAssetPath(assetPath);
        DeleteMetaFile(metaPath);

        RebuildDatabase();
        PersistCacheCatalog();
        NotifyAssetsChanged();
    }

    void HandleDeletedMeta(string metaPath)
    {
        CleanupImportedArtifactsForMetaPath(metaPath);
        RebuildDatabase();
        PersistCacheCatalog();
        NotifyAssetsChanged();
    }

}
