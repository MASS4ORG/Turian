namespace Turian.Editor.CLI;

/// <summary>
/// A project opened outside the Studio: asset database, scene manager and — when graphics are
/// requested — a headless Vulkan device. Scenes load through the same <see cref="SceneManager"/>
/// the Studio and the standalone runtime use.
/// </summary>
sealed class HeadlessProject : IDisposable
{
    readonly ILogger logger;

    /// <summary>Gets the project's build settings.</summary>
    public BuildAppSettings Settings { get; }

    /// <summary>Gets the asset database loaded from the project's cache catalog.</summary>
    public AssetDatabase Database { get; }

    /// <summary>Gets the scene manager scenes are loaded through.</summary>
    public SceneManager SceneManager { get; }

    /// <summary>Gets the headless Vulkan device, or <c>null</c> when the project was opened without graphics.</summary>
    public Vulkan? Vulkan { get; }

    HeadlessProject(BuildAppSettings settings, ILogger logger, bool withGraphics)
    {
        this.logger = logger;
        Settings = settings;

        // Engine.UI loads lazily; register its [TypeId] components (UiDocumentComponent, …) before
        // any scene deserialises so they don't fall back to MissingComponent.
        TypeRegistry.ScanAssembly(typeof(UiDocumentComponent).Assembly);

        // --reimport builds a database before this runs, and AssetDatabase is a throw-on-second-
        // construction singleton, so reuse that instance rather than making the two options
        // mutually exclusive.
        Database = AssetDatabase.TryGetInstance(out var existing) && existing is not null
            ? existing
            : new AssetDatabase();

        var catalogStatus = Database.LoadCatalogFromProject(settings.ProjectAbsoluteDir);
        if (catalogStatus is AssetCatalogLoadStatus.Unreadable)
        {
            throw new InvalidOperationException(
                $"The asset catalog under '{settings.CacheAbsoluteDir}' exists but could not be read, "
                + "so every asset it indexed is unavailable. Reimport the project to rebuild it.");
        }

        if (Database.Assets.Count == 0)
        {
            throw new InvalidOperationException(
                $"No asset catalog found under '{settings.CacheAbsoluteDir}'. Run with --reimport first.");
        }

        SceneManager = new SceneManager(Database);
        Vulkan = withGraphics ? new Vulkan(logger) : null;

        var services = new ServiceCollection()
            .AddSingleton<ISceneManager>(SceneManager)
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(Database))
            .AddSingleton(_ => LocalizationLoader.Create(settings, Database));

        if (Vulkan is not null)
        {
            _ = services.AddSingleton(Vulkan);
        }

        RuntimeServices.Configure(services.BuildServiceProvider());

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
        var started = Stopwatch.GetTimestamp();
        var root = LoadSceneRoot(sceneReference);

        logger.LogInformation(
            "Loaded scene '{SceneName}' in {Elapsed:F0} ms",
            root.Name,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return root;
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
    public void Dispose() => ModelAsset.ClearCache();
}
