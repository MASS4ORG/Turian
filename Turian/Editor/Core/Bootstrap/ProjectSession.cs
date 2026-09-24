namespace Turian.Editor.Core;

/// <summary>
/// Opens a project into the editor services: loads its settings, points the build manager and the
/// asset catalog at it, and restores the documents that were open last time.
/// </summary>
public sealed class ProjectSession(IServiceProvider services, ILogger log)
{
    /// <summary>The settings of the open project, or null when none has been opened.</summary>
    public AppSettings? Settings { get; private set; }

    /// <summary>
    /// Opens the project folder at <paramref name="projectPath"/>, after checking it with
    /// <see cref="ProjectValidator"/> and converting it if it still has a <c>project.data</c>.
    /// </summary>
    /// <param name="projectPath">The project folder, or a file inside it such as an older <c>project.data</c>.</param>
    /// <param name="openScene">An asset id or scene path to open on top of the restored session.</param>
    /// <returns>True if the project was valid and opened.</returns>
    public bool Open(string projectPath, string? openScene = null)
    {
        if (!ProjectValidator.Report(ProjectValidator.Validate(projectPath), log)) return false;

        var directory = SettingsService.ResolveProjectDirectory(projectPath)!;
        if (ProjectSettingsFiles.MigrateLegacyProject(directory))
            ProjectValidator.Report(ProjectValidator.Validate(directory), log);

        var loaded = SettingsService.Load(directory)!;
        ProjectSettingsLoader.LoadFromSources(loaded);

        var settingsService = services.GetRequiredService<SettingsService>();
        Settings = settingsService.Set(loaded);

        services.GetRequiredService<BuildManager>().UpdateSettings(Settings);
        RestoreAssetCatalog(services.GetRequiredService<AssetDatabase>(), Settings);

        // String tables live in the asset catalog, so the project localization can only be loaded once
        // the catalog is indexed. Hosts that do not register a LocaleService simply skip this.
        if (services.GetService(typeof(LocaleService)) is LocaleService locale)
            LocalizationLoader.Load(locale, Settings, services.GetRequiredService<AssetDatabase>());

        log.LogInformation("Opened project {Project}", Settings.ProjectAbsoluteDir);

        // Before any scene is read: a component whose type lives in the user assembly deserialises as
        // MissingComponent when that assembly has not been loaded yet.
        CompileUserScripts();

        RestoreSession(Settings);
        if (openScene is not null) OpenAsset(Settings, openScene);

        return true;
    }

    /// <summary>
    /// Points <paramref name="database"/> at the project's catalog, rebuilding it first when the
    /// cached one is damaged.
    /// </summary>
    /// <remarks>
    /// A catalog that exists but will not parse — a zero-byte file left behind by a crash or a
    /// forced reboot mid-write is the usual way this happens — otherwise loads as an empty database,
    /// and the editor opens the project reporting success while every scene comes up with nothing in
    /// it. Recovery is a full reimport rather than a rescan of the <c>.meta</c> files: child assets
    /// such as the individual meshes of a glTF live only in the catalog, so a rescan alone brings
    /// back the top-level assets and leaves those references dangling.
    /// </remarks>
    void RestoreAssetCatalog(AssetDatabase database, AppSettings settings)
    {
        if (database.LoadCatalogFromProject(settings.ProjectAbsoluteDir) is not AssetCatalogLoadStatus.Unreadable)
            return;

        log.LogWarning(
            "The asset catalog of {Project} is damaged; reimporting {Assets} to rebuild it",
            settings.ProjectAbsoluteDir,
            settings.AssetsAbsoluteDir);

        try
        {
            services.GetRequiredService<AssetImporter>().GenerateMetaFiles(settings.AssetsAbsoluteDir);
            log.LogInformation("Asset catalog rebuilt: {RecordCount} records recovered", database.Assets.Count);
        }
        catch (Exception exception)
        {
            log.LogError(
                exception,
                "Could not rebuild the asset catalog. Scenes will open empty until the project is reimported");
        }
    }

    /// <summary>
    /// Creates a project scaffold at <paramref name="projectDirectory"/> and opens it.
    /// </summary>
    /// <remarks>
    /// The scaffold is awaited inline rather than handed to the background runner: it is a handful of
    /// small writes, and the project has to exist before <see cref="Open"/> can be told about it.
    /// </remarks>
    /// <param name="projectDirectory">The folder to create the project in. It need not exist yet.</param>
    /// <returns>True when the scaffold was written and the project opened.</returns>
    public bool Create(string projectDirectory)
    {
        var created = services.GetRequiredService<ProjectBootstrapper>()
            .CreateAsync(projectDirectory).GetAwaiter().GetResult();

        if (created is not null) return Open(created);

        log.LogError("Could not create a project at {Directory}", projectDirectory);
        return false;
    }

    /// <summary>
    /// Compiles the project's scripts and swaps the user assembly in, tracked as a background task
    /// holding <see cref="EditorLocks.Scripts"/>. Safe to call repeatedly: a request arriving while a
    /// compile is in flight coalesces into a single rerun.
    /// </summary>
    /// <returns>The id of the compile task.</returns>
    public long RecompileScripts()
    {
        var build = services.GetRequiredService<BuildManager>();

        return services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec
            {
                Label = "Compile scripts",
                Kind = BackgroundTaskKind.Compile,
                Locks = EditorLocks.Scripts,
                BlocksUi = true,
                Policy = DuplicatePolicy.Coalesce,
            },
            async (progress, _) =>
            {
                progress.Report(0, "Building user assembly");
                var status = await build.CompileAndLoadAssemblyAsync().ConfigureAwait(false);
                if (status.State != BuildTaskState.Succeeded)
                    throw new InvalidOperationException(status.Message);

                progress.Report(1, status.Message);
            });
    }

    /// <summary>Writes the open documents to the project's session file.</summary>
    public void SaveSession()
    {
        if (Settings is null) return;

        var workspace = services.GetRequiredService<AssetWorkspace>();
        services.GetRequiredService<WorkspaceSessionStore>()
            .Save(Settings.ProjectAbsoluteDir, workspace.Capture());
    }

    /// <summary>Opens the configured startup scene, or the first prefab in the asset catalog.</summary>
    /// <returns><c>true</c> when a scene was resolved and opened.</returns>
    public bool OpenStartupScene()
    {
        if (Settings is null) return false;

        var reference = Settings.Get<PlayerSettings>().StartupScene?.AssetId;
        if (reference is not null && reference.Value != Guid.Empty)
        {
            OpenAsset(Settings, reference.Value.ToString());
            return services.GetRequiredService<SceneTreeController>().CurrentSceneRoot is not null;
        }

        var firstScene = services.GetRequiredService<AssetDatabase>().GetAssetsSnapshot()
            .Select(AssetReferenceQuery.CreateAsset)
            .OfType<Prefab>()
            .OrderBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (firstScene is null) return false;

        services.GetRequiredService<AssetWorkspace>().Open(firstScene);
        LogOpenedScene(firstScene);
        return true;
    }

    void CompileUserScripts()
    {
        var id = RecompileScripts();
        var tasks = services.GetRequiredService<BackgroundTaskManager>();

        // Opening blocks on this rather than racing it: every scene read after this point needs the
        // component types it defines.
        while (tasks.Get(id) is { IsActive: true }) Thread.Sleep(25);

        if (tasks.Get(id) is { Status: BackgroundTaskStatus.Failed } failed)
            log.LogWarning("User scripts did not compile: {Message}. Components from them will be missing",
                failed.Note);
    }

    void RestoreSession(AppSettings settings)
    {
        var session = services.GetRequiredService<WorkspaceSessionStore>().Load(settings.ProjectAbsoluteDir);
        if (session.OpenAssetIds.Count == 0)
        {
            log.LogInformation("No documents were open last time; nothing to restore");
            return;
        }

        var database = services.GetRequiredService<AssetDatabase>();
        var workspace = services.GetRequiredService<AssetWorkspace>();

        foreach (var assetId in session.OpenAssetIds)
        {
            if (Resolve(database, assetId) is not { } asset)
            {
                log.LogWarning("Asset {AssetId} from the last session is no longer in the catalog", assetId);
                continue;
            }

            workspace.Open(asset);
        }

        if (session.ActiveAssetId is { } activeId && Resolve(database, activeId) is { } active)
            workspace.Open(active);

        log.LogInformation("Restored {Count} open document(s)", workspace.Documents.Count);
    }

    void OpenAsset(AppSettings settings, string reference)
    {
        var database = services.GetRequiredService<AssetDatabase>();

        var asset = Guid.TryParse(reference, out var assetId)
            ? Resolve(database, assetId)
            : ResolveByPath(database, settings, reference);

        if (asset is null)
        {
            log.LogWarning("Could not resolve {Reference} to an asset in this project", reference);
            return;
        }

        services.GetRequiredService<AssetWorkspace>().Open(asset);
        LogOpenedScene(asset);
    }

    void LogOpenedScene(Asset asset)
    {
        var root = services.GetRequiredService<SceneTreeController>().CurrentSceneRoot;
        if (root is null) log.LogInformation("Opened {Asset}", asset.DisplayName);
        else log.LogInformation("Loaded scene {Scene} with {Children} child node(s)", root.Name, root.Children.Count);
    }

    static Asset? Resolve(AssetDatabase database, Guid assetId) =>
        database.TryGetAsset(assetId, out var record) && record is not null
            ? AssetReferenceQuery.CreateAsset(record)
            : null;

    static Asset? ResolveByPath(AssetDatabase database, AppSettings settings, string reference)
    {
        var absolute = Path.GetFullPath(reference, settings.ProjectAbsoluteDir);

        return database.GetAssetsSnapshot()
            .Select(AssetReferenceQuery.CreateAsset)
            .FirstOrDefault(asset => asset is not null && SamePath(settings, asset, absolute));
    }

    static bool SamePath(AppSettings settings, Asset asset, string absolute) =>
        string.Equals(
            Path.GetFullPath(Path.Combine(settings.ProjectAbsoluteDir, asset.RelativePath)),
            absolute,
            StringComparison.OrdinalIgnoreCase);
}
