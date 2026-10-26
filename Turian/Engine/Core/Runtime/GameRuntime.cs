namespace Turian.Engine.Core;

/// <summary>
/// The engine side of a running game, shared by every player: asset catalog, project settings, scene services,
/// input actions, localization and the scene ticker. A player adds its loop, and for a client its window,
/// renderer and input device.
/// </summary>
public sealed class GameRuntime
{
    GameRuntime(string projectDirectory, IContentDirectory content, AssetDatabase assets, AppSettings settings,
        SceneManager scenes)
    {
        ProjectDirectory = projectDirectory;
        Content = content;
        Assets = assets;
        Settings = settings;
        Scenes = scenes;
    }

    /// <summary>The folder the game runs from.</summary>
    public string ProjectDirectory { get; }

    /// <summary>Where the game's packaged content is read from.</summary>
    public IContentDirectory Content { get; }

    /// <summary>The game's asset catalog.</summary>
    public AssetDatabase Assets { get; }

    /// <summary>The project settings the game's settings index lists.</summary>
    public AppSettings Settings { get; }

    /// <summary>The loaded scenes.</summary>
    public SceneManager Scenes { get; }

    /// <summary>Drives the loaded scenes; a player calls <see cref="SceneTicker.Tick"/> once per frame.</summary>
    public SceneTicker Ticker { get; private set; } = null!;

    /// <summary>The project's action maps, read from the input source the player supplied.</summary>
    public InputActionService Actions { get; private set; } = null!;

    /// <summary>Resolves the game's localized text.</summary>
    public LocaleService Locale { get; } = new();

    /// <summary>The services scene components are injected from.</summary>
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>The startup scene's root, or <c>null</c> before <see cref="LoadStartupScene"/> or without one.</summary>
    public Node? StartupRoot { get; private set; }

    /// <summary>The window title: the product name, else the project title.</summary>
    public string ProductName => Settings.Get<PlayerSettings>().ProductName is { Length: > 0 } product
        ? product
        : Settings.Title ?? string.Empty;

    /// <summary>
    /// Opens the game in <paramref name="context"/>: reads its asset catalog and settings, and binds the scene
    /// services, including the game's engine modules and the player's own.
    /// </summary>
    /// <param name="context">The launcher's project folder, logger and service container.</param>
    /// <param name="input">The player's input device; a headless player passes a neutral source.</param>
    /// <param name="addServices">Registers the player's services, such as its render device.</param>
    /// <returns>The opened game, with no scene loaded yet.</returns>
    public static GameRuntime Start(GamePlayerContext context, IInputSource input,
        Action<IServiceCollection>? addServices = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        var content = context.ContentDirectory;
        var assets = new AssetDatabase();
        LoadCatalog(assets, content, context.ProjectDirectory);
        var loader = new RuntimeAssetLoader(assets);
        var settings = new AppSettings
        {
            ProjectAbsoluteDir = context.ProjectDirectory,
            Title = Path.GetFileName(context.ProjectDirectory),
        };
        ProjectSettingsLoader.Load(settings, loader, content);

        var runtime = new GameRuntime(context.ProjectDirectory, content, assets, settings, new SceneManager(assets));
        runtime.BindServices(context, loader, input, addServices);
        LogBuild(context.Logger, settings.Get<TimeSettings>());
        return runtime;
    }

    /// <summary>
    /// Logs the engine and game builds with the simulation budget in force, so every log, benchmark included, names
    /// exactly what produced it.
    /// </summary>
    static void LogBuild(ILogger logger, TimeSettings time)
    {
        TypeRegistry.TryGetType("Usercode.Game", out var gameType);
        logger.LogInformation(
            "Engine {Engine}, game {Game}; fixed step {FixedDeltaTime:F4} s, at most {MaxTicksPerFrame} tick(s) per frame, drop backlog {DropBacklog}",
            BuildVersion(typeof(GameRuntime).Assembly), BuildVersion(gameType?.Assembly), time.FixedDeltaTime,
            time.MaxTicksPerFrame, time.DropBacklog);
    }

    /// <summary>The informational version of <paramref name="assembly"/>, which carries the source commit when built from git.</summary>
    internal static string BuildVersion(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString()
        ?? "none";

    /// <summary>Loads the startup scene the player settings name, if any.</summary>
    public void LoadStartupScene()
    {
        if (Settings.Get<PlayerSettings>().StartupScene is not { IsEmpty: false } startupScene)
        {
            return;
        }

        var load = Scenes.LoadSceneAsync(startupScene.AssetId);
        while (!load.IsCompleted)
        {
            Ticker.Async.PumpPostedWork();
            Thread.Sleep(1);
        }
        StartupRoot = load.GetAwaiter().GetResult().RootNode;
    }

    void BindServices(GamePlayerContext context, IAssetLoader loader, IInputSource input,
        Action<IServiceCollection>? addServices)
    {
        Actions = new InputActionService(input);
        var clock = new SimulationClock(Settings.Get<TimeSettings>());
        var registrations = new ServiceCollection()
            .AddSingleton<IAppSettings>(Settings)
            .AddSingleton<LayerFilter>()
            .AddSingleton<IPhysicsQueries, PhysicsQueries>()
            .AddSingleton(Assets)
            .AddSingleton<ISceneManager>(Scenes)
            .AddSingleton(loader)
            .AddSingleton(clock)
            .AddSingleton<GameLoopScheduler>()
            .AddSingleton(input)
            .AddSingleton(Actions)
            .AddSingleton<IInputActions>(Actions)
            .AddSingleton(Locale);
        addServices?.Invoke(registrations);
        if (TypeRegistry.TryGetType("Usercode.Game", out var gameType) && gameType is not null)
        {
            registrations.AddEngineModules([.. EngineServiceModules.Graph(gameType.Assembly)]);
        }

        Services = context.BuildServices(registrations);
        Ticker = new SceneTicker(Scenes, clock)
        {
            Async = Services.GetRequiredService<GameLoopScheduler>(),
            Simulation = Services.GetService<SimulationSession>(),
            Systems = [.. Services.GetServices<IFixedTickSystem>()],
            InputSource = input,
            Actions = Actions,
        };
        Scenes.BindServices(Services);

        LocalizationLoader.Load(Locale, Settings, Assets);
        LoadActionMaps(loader);
    }

    /// <summary>
    /// Puts the project's action maps in force, then lets the player's own rebindings, stored beside the game's
    /// user data, replace what was authored.
    /// </summary>
    void LoadActionMaps(IAssetLoader loader)
    {
        Actions.Load(InputActionsLoader.Resolve(Settings, loader));

        if (Actions.Asset is not null && Settings.Get<PlayerSettings>().ProductName is { Length: > 0 } product)
        {
            InputBindingStore.Load(Actions, InputBindingStore.DefaultPath(product));
        }
    }

    // A built game ships its runtime catalog in Content/; a game run from its project reads the editor's cache.
    static void LoadCatalog(AssetDatabase assets, IContentDirectory content, string projectDirectory)
    {
        if (content.Exists(AssetDatabase.RuntimeCatalogPath))
        {
            assets.LoadRuntimeCatalog(content);
        }
        else if (content.Exists(".Cache/assetCatalog.json"))
        {
            assets.LoadCatalogFromProject(projectDirectory);
        }
        else
        {
            assets.BuildDatabase(Path.Combine(projectDirectory, "Assets"));
        }
    }
}
