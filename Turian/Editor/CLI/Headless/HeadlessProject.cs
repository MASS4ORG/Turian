namespace Turian.Editor.CLI;

/// <summary>
/// A project opened outside the Studio: asset database, scene manager and — when graphics are
/// requested — a headless Vulkan device. Scenes load through the same <see cref="SceneManager"/>
/// the Studio and the standalone runtime use.
/// </summary>
sealed class HeadlessProject : IDisposable
{
    readonly ILogger logger;
    readonly ServiceProvider services;

    /// <summary>Gets the project's build settings.</summary>
    public BuildAppSettings Settings { get; }

    /// <summary>Gets the asset database loaded from the project's cache catalog.</summary>
    public AssetDatabase Database { get; }

    /// <summary>Gets the scene manager scenes are loaded through.</summary>
    public SceneManager SceneManager { get; }

    /// <summary>Gets the headless Vulkan device, or <c>null</c> when the project was opened without graphics.</summary>
    public Vulkan? Vulkan { get; }

    /// <summary>Gets the project's loaded locale service.</summary>
    public LocaleService Locale => services.GetRequiredService<LocaleService>();

    HeadlessProject(BuildAppSettings settings, ILogger logger, bool withGraphics)
    {
        this.logger = logger;
        Settings = settings;

        // The bricks' [TypeId] types must be registered before any scene deserialises, or their components
        // fall back to MissingComponent.
        BrickAssemblies.Load(ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir), logger);

        // --reimport writes the catalog this reads, so the two options combine without sharing an instance.
        Database = new AssetDatabase();

        var catalogStatus = Database.LoadCatalogFromProject(settings.ProjectAbsoluteDir);
        if (catalogStatus is AssetCatalogLoadStatus.Unreadable)
        {
            throw new InvalidOperationException(
                $"The asset catalog under '{settings.CacheAbsoluteDir}' exists but could not be read, "
                + "so every asset it indexed is unavailable. Reimport the project to rebuild it.");
        }

        AssetImporter.RepairCachedCatalog(Database, settings, logger);

        if (Database.Assets.Count == 0)
        {
            throw new InvalidOperationException(
                $"No asset catalog found under '{settings.CacheAbsoluteDir}'. Run with --reimport first.");
        }

        SceneManager = new SceneManager(Database);
        Vulkan = withGraphics ? new Vulkan(logger) : null;

        var registrations = new ServiceCollection()
            .AddSingleton<IAppSettings>(settings)
            .AddSingleton<LayerFilter>()
            .AddSingleton<IPhysicsQueries, PhysicsQueries>()
            .AddSingleton(Database)
            .AddSingleton<ISceneManager>(SceneManager)
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(Database))
            .AddSingleton(new SimulationClock(settings.Get<TimeSettings>()))
            .AddSingleton(_ => LocalizationLoader.Create(settings, Database));

        if (Vulkan is not null)
        {
            _ = registrations.AddSingleton(Vulkan);
        }

        if (TypeRegistry.TryGetType("Usercode.Game", out var gameType) && gameType is not null)
            registrations.AddEngineModules(gameType.Assembly);

        services = registrations.BuildServiceProvider();
        SceneManager.BindServices(services);

        logger.LogInformation(
            "Opened {Title}: {RecordCount} asset records from {CacheDir}",
            settings.Title,
            Database.Assets.Count,
            settings.CacheAbsoluteDir);
    }

    /// <summary>
    /// Opens <paramref name="settings"/>' project.
    /// </summary>
    /// <param name="settings">The project's build settings.</param>
    /// <param name="logger">The logger progress is reported through.</param>
    /// <param name="withGraphics">Whether to create a headless Vulkan device.</param>
    /// <returns>The opened project.</returns>
    public static HeadlessProject Open(BuildAppSettings settings, ILogger logger, bool withGraphics) =>
        new(settings, logger, withGraphics);

    /// <summary>
    /// Loads a scene and returns its root node.
    /// </summary>
    /// <param name="sceneReference">
    /// An asset id, a path to a serialized scene, or <c>null</c> to load the project's startup scene.
    /// </param>
    /// <returns>The loaded root node.</returns>
    public Node LoadScene(string? sceneReference)
    {
        return LoadSceneRoot(sceneReference);
    }

    Node LoadSceneRoot(string? sceneReference)
    {
        if (string.IsNullOrWhiteSpace(sceneReference))
        {
            var startup = Settings.Get<PlayerSettings>().StartupScene?.AssetId ?? Guid.Empty;
            if (startup == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "The project defines no StartupScene. Pass --scene with an asset id or a file path.");
            }

            return SceneManager.LoadNodeAsync(startup).GetAwaiter().GetResult();
        }

        if (Guid.TryParse(sceneReference, out var assetId))
        {
            return SceneManager.LoadNodeAsync(assetId).GetAwaiter().GetResult();
        }

        var path = Path.GetFullPath(sceneReference);
        return SceneManager.LoadNodeAsync(path).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        services.Dispose();
        ModelAsset.ClearCache();
    }
}
