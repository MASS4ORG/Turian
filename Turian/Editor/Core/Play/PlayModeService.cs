namespace Turian.Editor.Core;

/// <summary>
/// Runs the game inside the Studio process, Unity-style, instead of launching a separate window.
///
/// <para>
/// A session runs on a throwaway deep copy of the scene being edited, so the editor's own hierarchy
/// is never mutated and stopping play needs no restore step. Rendering is left to the Game panel,
/// while the session itself is pumped by <see cref="Tick()"/> from a window-level timer.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe: every method is expected to be called from the UI thread, which is also the
/// thread the Game panel ticks on.
/// </remarks>
public sealed class PlayModeService(
    IPlaySceneHost sceneTree,
    AssetDatabase assetDatabase,
    IServiceProvider editorServices,
    ILogger logger)
{
    readonly IPlaySceneHost sceneTree = sceneTree;
    readonly AssetDatabase assetDatabase = assetDatabase;
    readonly IServiceProvider editorServices = editorServices;
    readonly ILogger logger = logger;

    readonly Stopwatch clock = new();
    ServiceProvider? playServices;
    SceneTicker? ticker;
    ISceneManager? playSceneManager;
    double lastTickSeconds;

    /// <summary>Gets the current transport state.</summary>
    public PlayState State { get; private set; } = PlayState.Stopped;

    /// <summary>Gets the root of the running copy of the scene, or <c>null</c> when stopped.</summary>
    public Node? PlayRoot { get; private set; }

    /// <summary>Gets the input source the running game reads through <see cref="Input"/>.</summary>
    public BufferedInputSource Input { get; } = new();

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<PlayState>? StateChanged;

    /// <summary>Gets a value indicating whether a session is running, whether playing or paused.</summary>
    public bool IsActive => State != PlayState.Stopped;

    /// <summary>
    /// Gets the camera the Game panel should render from: the highest-<see cref="CameraComponent.Priority"/>
    /// camera currently in the running hierarchy, or <c>null</c> when the scene has none.
    /// </summary>
    /// <remarks>
    /// Resolved fresh on every access rather than cached — a script (such as a camera-cycling
    /// rig) can reassign <see cref="CameraComponent.Priority"/> mid-session to change which camera
    /// is primary, and caching the first result made every later reassignment silently do nothing.
    /// The walk is one small tree traversal per call; at one call per rendered frame that is not
    /// worth trading away correctness for.
    /// </remarks>
    public ICamera? ActiveCamera => PlayRoot is null ? null : CameraComponent.FindPrimary(PlayRoot);

    // ── Transport ──────────────────────────────────────────────────────────────

    /// <summary>Starts a session on a copy of the currently open scene.</summary>
    /// <returns><c>true</c> if a session started; <c>false</c> when no scene is open or the copy failed.</returns>
    public bool Start() => Start(null);

    /// <summary>
    /// Starts a session on a copy of the currently open scene, optionally overriding the locale the
    /// project's <see cref="LocalizationSettings"/> selects. Headless runs use the override to verify
    /// localization without touching the authored settings.
    /// </summary>
    /// <param name="localeOverride">A BCP-47 locale to force for the session, or <c>null</c> to use the project's default.</param>
    /// <returns><c>true</c> if a session started; <c>false</c> when no scene is open or the copy failed.</returns>
    public bool Start(string? localeOverride)
    {
        if (State != PlayState.Stopped) return false;

        var editorRoot = sceneTree.CurrentSceneRoot;
        if (editorRoot is null)
        {
            logger.LogWarning("Cannot enter play mode: no scene is open");
            return false;
        }

        Input.Clear();

        // The world must be fully built before any component wakes: scripts resolve engine services
        // (ISceneManager, IInputSource, ...) in OnAwake and cache them for the whole session, so the
        // play scope has to be live and the scene tracked before Awake runs.
        playServices = BuildPlayServices();
        var sceneManager = playServices.GetRequiredService<ISceneManager>();
        RuntimeServices.Configure(playServices);

        if (localeOverride is not null
            && playServices.GetService(typeof(LocaleService)) is LocaleService locale)
            locale.SetLocale(localeOverride);

        Node? clone;
        try
        {
            clone = NodeCloner.DeepClone(editorRoot, awake: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot enter play mode: failed to copy the scene");
            AbandonPlayServices();
            return false;
        }

        if (clone is null)
        {
            logger.LogError("Cannot enter play mode: copying the scene produced no root node");
            AbandonPlayServices();
            return false;
        }

        sceneManager.AdoptScene(sceneTree.CurrentAsset?.Id ?? Guid.NewGuid(), clone);
        clone.Awake(null);

        PlayRoot = clone;
        ticker = new SceneTicker(sceneManager)
        {
            InputSource = Input,
            Actions = playServices.GetService<InputActionService>(),
        };
        playSceneManager = sceneManager;
        clock.Restart();
        lastTickSeconds = 0d;

        // Point the Scene Tree and Inspector at the running objects, so their values can be watched
        // and tweaked live. Because play runs on a copy, the tweaks vanish when the session ends.
        sceneTree.ShowRuntimeScene(clone);

        SetState(PlayState.Playing);
        logger.LogInformation("Play mode started on a copy of scene {SceneName}", clone.Name);
        return true;
    }

    /// <summary>Freezes the running session. No-op unless currently playing.</summary>
    public void Pause()
    {
        if (State != PlayState.Playing) return;
        Input.Clear();
        SetState(PlayState.Paused);
    }

    /// <summary>Resumes a paused session. No-op unless currently paused.</summary>
    public void Resume()
    {
        if (State != PlayState.Paused) return;
        ticker?.ResetAccumulator();
        // Discard the time spent paused, otherwise the first frame back replays it all at once.
        lastTickSeconds = clock.Elapsed.TotalSeconds;
        SetState(PlayState.Playing);
    }

    /// <summary>Advances a paused session by exactly one frame. No-op unless currently paused.</summary>
    public void StepFrame()
    {
        if (State != PlayState.Paused) return;
        RunGuarded(() => ticker!.StepFrame());
        FollowActiveScene();
    }

    /// <summary>
    /// Ends the session and discards the running copy. The edited scene is untouched, so this is
    /// all the "revert on exit" that is needed.
    /// </summary>
    public void Stop()
    {
        if (State == PlayState.Stopped) return;

        PlayRoot = null;
        ticker = null;
        playSceneManager = null;
        clock.Reset();
        Input.Clear();

        RuntimeServices.Configure(editorServices);
        playServices?.Dispose();
        playServices = null;

        // Back to the edited scene; everything the session changed goes away with the copy.
        sceneTree.ShowEditorScene();

        SetState(PlayState.Stopped);
        logger.LogInformation("Play mode stopped. The edited scene was not modified");
    }

    // ── Frame ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the session using the session's own clock. Does nothing unless a session is actively
    /// playing, so the caller can pump it unconditionally.
    /// </summary>
    /// <remarks>
    /// The session is pumped by a window-level timer, not by the Game panel: the game must keep
    /// running while that panel is hidden behind another dock tab, closed, or being re-docked.
    /// </remarks>
    public void Tick()
    {
        if (State != PlayState.Playing) return;

        var now = clock.Elapsed.TotalSeconds;
        var deltaTime = now - lastTickSeconds;
        lastTickSeconds = now;

        Tick(deltaTime);
    }

    /// <summary>
    /// Advances the session by an explicit <paramref name="deltaTime"/>, in seconds.
    /// Prefer <see cref="Tick()"/>; this overload exists for deterministic tests.
    /// </summary>
    /// <param name="deltaTime">The time elapsed since the previous frame, in seconds.</param>
    public void Tick(double deltaTime)
    {
        if (State != PlayState.Playing) return;
        RunGuarded(() => ticker!.Tick(deltaTime));
        FollowActiveScene();
    }

    /// <summary>
    /// Re-points the panels when game code loads a different scene mid-session, so the Scene Tree
    /// keeps showing what is actually running rather than the hierarchy play started with.
    /// </summary>
    void FollowActiveScene()
    {
        if (State == PlayState.Stopped) return;

        var activeRoot = playSceneManager?.ActiveScene?.RootNode;
        if (activeRoot is null || ReferenceEquals(activeRoot, PlayRoot)) return;

        PlayRoot = activeRoot;
        sceneTree.ShowRuntimeScene(activeRoot);
        logger.LogInformation("Play mode followed a scene change to {SceneName}", activeRoot.Name);
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one tick, stopping the session if user code throws. A script bug should surface in the
    /// Output panel and drop the Studio back to edit mode, not spam an exception every frame.
    /// </summary>
    void RunGuarded(Action tick)
    {
        try
        {
            tick();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in play mode. Stopping play");
            Stop();
        }
    }

    /// <summary>
    /// Builds the service scope the running game resolves through <see cref="RuntimeServices"/>.
    /// Mirrors the standalone runtime's registration so scripts behave identically in both. The
    /// graphics device is inherited from the editor, so meshes uploaded during play land on the
    /// same headless device the viewports render with.
    /// </summary>
    ServiceProvider BuildPlayServices()
    {
        var services = new ServiceCollection()
            .AddSingleton(logger)
            .AddSingleton(assetDatabase)
            .AddSingleton<ISceneManager>(new SceneManager(assetDatabase))
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(assetDatabase))
            .AddSingleton<IInputSource>(Input)
            .AddSingleton(_ => new InputActionService(Input));

        if (editorServices.GetService(typeof(Vulkan)) is Vulkan vulkan)
            services.AddSingleton(vulkan);

        // The session's localization: a fresh service loaded from the project's settings and string
        // tables, so the play scope starts in the game's default locale without touching the editor's.
        if (ProjectSettings() is { } appSettings)
            services.AddSingleton(_ => LocalizationLoader.Create(appSettings, assetDatabase));

        var provider = services.BuildServiceProvider();
        LoadActionMaps(provider);
        return provider;
    }

    /// <summary>
    /// Puts the project's authored action maps in force for the session, so a script polling
    /// <see cref="InputActions"/> sees the same bindings a built game would.
    /// </summary>
    void LoadActionMaps(IServiceProvider provider)
    {
        // The project's own settings live on SettingsService; the container's IAppSettings is a blank
        // instance the editor keeps for anything that needs the type before a project is opened.
        if (ProjectSettings() is not { } settings) return;

        var maps = InputActionsLoader.Resolve(settings, provider.GetService<IAssetLoader>(),
            settings.ProjectAbsoluteDir);

        provider.GetRequiredService<InputActionService>().Load(maps);

        if (maps is not null)
            logger.LogInformation("Input actions: {MapCount} map(s), {ActionCount} action(s) in force",
                maps.Maps.Count, maps.Actions.Count());
    }

    /// <summary>
    /// The open project's settings. The Studio keeps them on <see cref="SettingsService"/> — its
    /// container's <see cref="IAppSettings"/> is a blank instance registered before a project is
    /// opened — while the headless CLI registers the real ones under the interface.
    /// </summary>
    IAppSettings? ProjectSettings()
    {
        if (editorServices.GetService(typeof(SettingsService)) is SettingsService { Settings: { } loaded })
            return loaded;

        return editorServices.GetService(typeof(IAppSettings)) is IAppSettings
        { ProjectAbsoluteDir.Length: > 0 } settings
            ? settings
            : null;
    }

    /// <summary>
    /// Tears the play scope back down after a failed start, so a refused Play leaves the editor
    /// resolving its own services again.
    /// </summary>
    void AbandonPlayServices()
    {
        RuntimeServices.Configure(editorServices);
        playServices?.Dispose();
        playServices = null;
    }

    void SetState(PlayState newState)
    {
        if (State == newState) return;
        State = newState;
        StateChanged?.Invoke(newState);
    }
}
