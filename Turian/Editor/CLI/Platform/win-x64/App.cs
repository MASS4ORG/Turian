using Silk.NET.Maths;

namespace Turian;

/// <summary>
/// Main loop app
/// </summary>
public class App : IDisposable
{
    readonly ILogger logger;
    readonly WindowManager windowManager;
    readonly RendererManager rendererManager;
    readonly AssetDatabase assetDatabase;
    readonly InputManager inputManager;
    readonly Vulkan vulkan;
    readonly RuntimeProjectOptions runtimeProjectOptions;
    readonly string projectDirectory;
    readonly string assetFolder;
    readonly string cacheRootDirectory;
    readonly string runtimeContentDirectory;
    Node? nodeMain;
    UiManager? uiManager;
    SceneManager sceneManager = null!;
    SceneTicker sceneTicker = null!;
    SilkInputSource inputSource = null!;
    InputActionService actions = null!;
    LocaleService locale = null!;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="rendererManager"></param>
    /// <param name="windowManager"></param>
    /// <param name="assetDatabase"></param>
    /// <param name="inputManager"></param>
    /// <param name="vulkan"></param>
    /// <param name="runtimeProjectOptions"></param>
    /// <param name="logger"></param>
    /// <exception cref="Exception"></exception>
    public App(
        RendererManager rendererManager,
        WindowManager windowManager,
        AssetDatabase assetDatabase,
        InputManager inputManager,
        Vulkan vulkan,
        RuntimeProjectOptions runtimeProjectOptions,
        ILogger logger
    )
    {
        this.logger = logger;

        this.windowManager = windowManager;
        this.vulkan = vulkan;
        this.rendererManager = rendererManager;
        this.inputManager = inputManager;
        this.assetDatabase = assetDatabase;
        this.runtimeProjectOptions = runtimeProjectOptions;

        projectDirectory = GetProjectPath();
        assetFolder = Path.Combine(projectDirectory, "Assets");
        cacheRootDirectory = Path.Combine(projectDirectory, ".Cache");
        runtimeContentDirectory = Path.Combine(projectDirectory, "Content");
    }

    /// <summary>
    /// Initialize all needed services and start the main loop
    /// </summary>
    public void Initialize()
    {
#if DEBUG
        // Development builds (Build & Run) show the window size and frame rate in the title.
        windowManager.ShowStats = true;
#endif
        windowManager.Initialize();
        windowManager.Window.Render += Render;
        windowManager.Window.FramebufferResize += Resize;
        rendererManager.Initialize();

        sceneManager = new SceneManager(assetDatabase);
        var assetLoader = new RuntimeAssetLoader(assetDatabase);
        inputSource = new SilkInputSource(inputManager);
        actions = new InputActionService(inputSource);
        sceneTicker = new SceneTicker(sceneManager) { InputSource = inputSource, Actions = actions };
        uiManager = new UiManager(vulkan);
        locale = new LocaleService();

        RuntimeServices.Configure(new ServiceCollection()
            .AddSingleton(vulkan)
            .AddSingleton<ISceneManager>(sceneManager)
            .AddSingleton<IAssetLoader>(assetLoader)
            .AddSingleton<IInputSource>(inputSource)
            .AddSingleton(actions)
            .AddSingleton(locale)
            .BuildServiceProvider());

        CreateAssetDatabase();

        RegisterUserCodeTypes();

        LoadStartupScene();
        SetWindowIcon();
        logger.Lap("startup", "objects loaded");

        inputManager.OnKeyPressed += OnKeyPressed;

        Resize(windowManager.Window.FramebufferSize);

        inputManager.Initalize();
        inputSource.Initialize();

        vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);

        windowManager.Window.Run();
    }

    void CreateAssetDatabase()
    {
        if (HasRuntimeAssetCatalog())
        {
            assetDatabase.LoadRuntimeCatalog(projectDirectory);
            return;
        }

        if (HasProjectCacheCatalog())
        {
            assetDatabase.LoadCatalogFromProject(projectDirectory);
            return;
        }

        assetDatabase.BuildDatabase(assetFolder);
    }

    bool HasRuntimeAssetCatalog()
    {
        return Directory.Exists(runtimeContentDirectory)
               && File.Exists(Path.Combine(runtimeContentDirectory, "assetCatalog.json"));
    }

    bool HasProjectCacheCatalog()
    {
        return Directory.Exists(cacheRootDirectory)
               && File.Exists(Path.Combine(cacheRootDirectory, "assetCatalog.json"));
    }

    /// <summary>
    /// Puts the project's action maps in force, then lets the player's own rebindings, stored beside
    /// the game's user data, replace what was authored.
    /// </summary>
    void LoadActionMaps(IAppSettings? projectSettings)
    {
        if (projectSettings is null) return;

        actions.Load(InputActionsLoader.Resolve(projectSettings, RuntimeServices.TryGet<IAssetLoader>(),
            projectDirectory));

        if (actions.Asset is not null && projectSettings.Get<PlayerSettings>().ProductName is { Length: > 0 } product)
            InputBindingStore.Load(actions, InputBindingStore.DefaultPath(product));
    }

    void LoadStartupScene()
    {
        var projectSettings = LoadProjectSettings();
        windowManager.Title = projectSettings.Get<PlayerSettings>().ProductName is { Length: > 0 } productName
            ? productName
            : projectSettings.Title ?? windowManager.Title;
        LocalizationLoader.Load(locale, projectSettings, assetDatabase);
        LoadActionMaps(projectSettings);

        if (projectSettings.Get<PlayerSettings>().StartupScene is { IsEmpty: false } startupScene)
        {
            var loadedScene = sceneManager.LoadSceneAsync(startupScene.AssetId)
                .GetAwaiter()
                .GetResult();

            nodeMain = loadedScene.RootNode;
        }
    }

    /// <summary>Gives the window the icon its build wrote beside the game, when the project has one.</summary>
    void SetWindowIcon()
    {
        var iconPath = Path.Combine(projectDirectory, PlayerSettings.BuiltIconFileName);
        if (!File.Exists(iconPath) || windowManager.Window is not IWindow window) return;

        var image = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(iconPath),
            StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var icon = new Silk.NET.Core.RawImage(image.Width, image.Height, image.Data);
        window.SetWindowIcon(ref icon);
    }

    void RegisterUserCodeTypes()
    {
        // Engine.UI loads lazily; register its [TypeId] types (UiDocumentComponent, UiDocumentAsset,
        // …) before the startup scene and asset catalog deserialise.
        TypeRegistry.ScanAssembly(typeof(UiDocumentComponent).Assembly);

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "usercode.typeids.json");
        TypeRegistry.RegisterFromManifest(manifestPath, logger);
    }

    string GetProjectPath()
    {
        var configuredProjectPath = runtimeProjectOptions.ProjectPath;

        if (string.IsNullOrWhiteSpace(configuredProjectPath))
        {
            throw new InvalidOperationException("Missing project folder.");
        }

        var fullProjectPath = Path.GetFullPath(configuredProjectPath);

        if (!Directory.Exists(fullProjectPath))
        {
            throw new DirectoryNotFoundException($"The project folder {fullProjectPath} does not exist.");
        }

        return fullProjectPath;
    }

    /// <summary>Reads the settings assets the game's folder lists, through the asset catalog.</summary>
    AppSettings LoadProjectSettings()
    {
        var projectSettings = new AppSettings
        {
            ProjectAbsoluteDir = projectDirectory,
            Title = Path.GetFileName(projectDirectory),
        };
        ProjectSettingsLoader.Load(projectSettings, RuntimeServices.TryGet<IAssetLoader>());

        return projectSettings;
    }

    // FIXME: does it do anything really?
    void OnKeyPressed(Key key)
    {
        if (key == Key.Escape)
        {
            windowManager.Window.Close();
        }
    }

    void Render(double deltaTime)
    {
        sceneTicker.Tick(deltaTime);

        var uiRoot = nodeMain ?? sceneManager.ActiveScene?.RootNode ?? sceneManager.PersistentRoot;
        var fb = windowManager.Window.FramebufferSize;
        if (fb.X > 0 && fb.Y > 0)
        {
            rendererManager.OverlayTexture = uiManager!.TryRenderOverlay(uiRoot, fb.X, fb.Y, (float)deltaTime);
        }

        var allRoots = sceneManager.LoadedScenes
            .Select(s => s.RootNode)
            .Prepend(sceneManager.PersistentRoot);

        foreach (var root in allRoots)
        {
            // One camera per root: they all draw to the same swapchain image, so rendering every
            // camera in the scene just overdraws — which is what a scene with several enabled
            // cameras looks like as flicker.
            var camera = CameraComponent.FindPrimary(root);
            if (camera is not null)
            {
                rendererManager.Render(deltaTime, camera, root);
            }
        }
    }

    void Resize(Vector2D<int> newSize)
    {
        nodeMain = sceneManager.ActiveScene?.RootNode;

        if (nodeMain is null)
        {
            return;
        }

        foreach (var camera in Node.GetComponentsInChildren<CameraComponent>(nodeMain))
        {
            camera.Resize((uint)newSize.X, (uint)newSize.Y);
        }
    }

    /// <summary>
    /// Destroy data and free memory
    /// </summary>
    public void Dispose()
    {
        uiManager?.Dispose();
        windowManager.Dispose();
        rendererManager.Dispose();
        inputSource.Dispose();
        inputManager.Dispose();
        vulkan.Device.Dispose();

        GC.SuppressFinalize(this);
    }
}
