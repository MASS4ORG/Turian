namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="PlayModeService"/>, the Studio's in-editor play mode: its transport state
/// machine, and the guarantee that a session never mutates the scene being edited.
/// </summary>
public class PlayModeServiceTests : IDisposable
{
    /// <summary>Counts frames so tests can assert exactly how far a session advanced.</summary>
    [TypeId("a4000003-0000-4000-8000-000000000001")]
    sealed class FrameCountingComponent : Component
    {
        public static int TotalUpdates { get; set; }

        public override void OnUpdate(float deltaTime)
        {
            TotalUpdates++;
            var node = Node ?? throw new InvalidOperationException("FrameCountingComponent must be attached to a node.");
            node.Transform.Position += new Vector3(1f, 0f, 0f);
        }
    }

    /// <summary>
    /// Mirrors the sample project's <c>SceneLoader</c>: resolve engine services in
    /// <see cref="Component.OnAwake"/>, cache them, then use them from a later frame.
    /// </summary>
    [TypeId("a4000003-0000-4000-8000-000000000002")]
    sealed class ServiceCachingComponent : Component
    {
        public static ISceneManager? ResolvedAtAwake { get; set; }
        public static IInputSource? InputResolvedAtAwake { get; set; }

        ISceneManager? sceneManager;

        public override void OnAwake()
        {
            sceneManager = RuntimeServices.TryGet<ISceneManager>();
            ResolvedAtAwake = sceneManager;
            InputResolvedAtAwake = RuntimeServices.TryGet<IInputSource>();
        }

        public override void OnUpdate(float deltaTime)
        {
            // Would throw a NullReferenceException if Awake ran before the play scope existed.
            _ = sceneManager!.LoadedScenes.Count();
        }
    }

    /// <summary>An empty provider: play mode only probes it for optional editor services.</summary>
    sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    readonly AssetDatabase assetDatabase;
    readonly SceneTreeController sceneTree;
    readonly PlayModeService playMode;

    /// <summary>Starts each test from a clean singleton and runtime-service state.</summary>
    public PlayModeServiceTests()
    {
        TestAssetDatabase.Reset();
        RuntimeServices.Reset();
        FrameCountingComponent.TotalUpdates = 0;

        assetDatabase = new AssetDatabase();
        // The importer is only reached through SceneTreeController.SaveAsset, which play mode never
        // calls; constructing a real one would need the BuildManager singleton.
        sceneTree = new SceneTreeController(new AssetManager(), new SettingsService(), assetImporter: null!);

        playMode = new PlayModeService(sceneTree, assetDatabase, new EmptyServiceProvider(), Log.Logger);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        playMode.Stop();
        RuntimeServices.Reset();
        TestAssetDatabase.Reset();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Opens a scene in the controller. The Studio does this through <see cref="SceneTreeController.OpenAsset"/>,
    /// which needs a prefab on disk; setting the backing field keeps the test to the play-mode behavior.
    /// </summary>
    Node GiveControllerAnOpenScene()
    {
        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Mover" };
        child.AddComponent(new FrameCountingComponent());
        root.Children.Add(child);
        root.Awake(null);

        typeof(SceneTreeController)
            .GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);

        return root;
    }

    /// <summary>
    /// Scripts resolve engine services in <c>OnAwake</c> and cache them for the session, so the play
    /// scope must be live before any component wakes. Getting this backwards handed scripts a null
    /// <see cref="ISceneManager"/>, which only blew up later — when the script first used it.
    /// </summary>
    [Fact]
    public void ComponentsResolveEngineServicesDuringAwake()
    {
        ServiceCachingComponent.ResolvedAtAwake = null;
        ServiceCachingComponent.InputResolvedAtAwake = null;

        var root = new Node { Name = "Root" };
        root.AddComponent(new ServiceCachingComponent());
        root.Awake(null);
        typeof(SceneTreeController)
            .GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);

        Assert.True(playMode.Start());

        Assert.NotNull(ServiceCachingComponent.ResolvedAtAwake);
        Assert.Same(playMode.Input, ServiceCachingComponent.InputResolvedAtAwake);

        // The frame that uses the cached service must not tear the session down.
        playMode.Tick(0.016);
        Assert.Equal(PlayState.Playing, playMode.State);
    }

    /// <summary>The scene is tracked before Awake, so a script can see it from its first callback.</summary>
    [Fact]
    public void TheRunningSceneIsTrackedBeforeComponentsWake()
    {
        ServiceCachingComponent.ResolvedAtAwake = null;

        var root = new Node { Name = "Root" };
        root.AddComponent(new ServiceCachingComponent());
        root.Awake(null);
        typeof(SceneTreeController)
            .GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);

        playMode.Start();

        Assert.NotNull(ServiceCachingComponent.ResolvedAtAwake!.ActiveScene);
    }

    /// <summary>Play cannot start without an open scene, and reports the refusal rather than throwing.</summary>
    [Fact]
    public void Start_WithoutAnOpenScene_DoesNotEnterPlayMode()
    {
        Assert.False(playMode.Start());
        Assert.Equal(PlayState.Stopped, playMode.State);
        Assert.Null(playMode.PlayRoot);
    }

    /// <summary>Starting runs on a copy, not on the editor's own hierarchy.</summary>
    [Fact]
    public void Start_RunsOnACopyOfTheEditedScene()
    {
        var editorRoot = GiveControllerAnOpenScene();

        Assert.True(playMode.Start());

        Assert.Equal(PlayState.Playing, playMode.State);
        Assert.NotNull(playMode.PlayRoot);
        Assert.NotSame(editorRoot, playMode.PlayRoot);
    }

    /// <summary>Ticking advances the session; pausing freezes it; resuming continues it.</summary>
    [Fact]
    public void Transport_PauseFreezesAndResumeContinues()
    {
        GiveControllerAnOpenScene();
        playMode.Start();

        playMode.Tick(0.016);
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        playMode.Pause();
        Assert.Equal(PlayState.Paused, playMode.State);

        playMode.Tick(0.016);
        playMode.Tick(0.016);
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        playMode.Resume();
        Assert.Equal(PlayState.Playing, playMode.State);

        playMode.Tick(0.016);
        Assert.Equal(2, FrameCountingComponent.TotalUpdates);
    }

    /// <summary>A frame step advances exactly one frame, and only while paused.</summary>
    [Fact]
    public void StepFrame_AdvancesOneFrameAndOnlyWhilePaused()
    {
        GiveControllerAnOpenScene();
        playMode.Start();

        playMode.StepFrame(); // ignored: the session is playing, not paused
        Assert.Equal(0, FrameCountingComponent.TotalUpdates);

        playMode.Pause();
        playMode.StepFrame();
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        playMode.StepFrame();
        Assert.Equal(2, FrameCountingComponent.TotalUpdates);
    }

    /// <summary>
    /// The acceptance criterion for "exiting play mode restores the editor scene": after a session
    /// that moves a node every frame, the edited scene serializes exactly as it did before play.
    /// </summary>
    [Fact]
    public void Stop_LeavesTheEditedSceneExactlyAsItWas()
    {
        var editorRoot = GiveControllerAnOpenScene();
        var before = Serializer.Serialize(editorRoot);

        playMode.Start();
        playMode.Tick(0.016);
        playMode.Tick(0.016);
        playMode.Tick(0.016);
        playMode.Stop();

        Assert.Equal(PlayState.Stopped, playMode.State);
        Assert.Null(playMode.PlayRoot);
        Assert.Equal(before, Serializer.Serialize(editorRoot));
        Assert.Equal(0f, editorRoot.Children[0].Transform.Position.X);
        Assert.Equal(3, FrameCountingComponent.TotalUpdates);
    }

    /// <summary>Ticking after a stop does nothing, so a stale panel timer cannot revive a session.</summary>
    [Fact]
    public void Tick_AfterStop_DoesNothing()
    {
        GiveControllerAnOpenScene();
        playMode.Start();
        playMode.Stop();

        playMode.Tick(0.016);

        Assert.Equal(0, FrameCountingComponent.TotalUpdates);
    }

    /// <summary>
    /// A session keeps advancing while the scene being edited changes underneath it. Play runs on
    /// the copy taken at <see cref="PlayModeService.Start()"/>, so opening another scene in the editor
    /// must not disturb or detach the running game.
    /// </summary>
    [Fact]
    public void ChangingTheEditedScene_DoesNotDisturbTheRunningSession()
    {
        GiveControllerAnOpenScene();
        playMode.Start();
        var playRoot = playMode.PlayRoot;

        playMode.Tick(0.016);
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        // The user switches away from the scene that was playing.
        sceneTree.OpenAsset(null);

        playMode.Tick(0.016);

        Assert.Equal(PlayState.Playing, playMode.State);
        Assert.Same(playRoot, playMode.PlayRoot);
        Assert.Equal(2, FrameCountingComponent.TotalUpdates);

        // The play controls key off this, so a running session must stay stoppable even once the
        // focused tab is no longer a scene.
        Assert.NotNull(sceneTree.CurrentSceneRoot);
    }

    /// <summary>
    /// While playing, the Scene Tree and Inspector must resolve the <em>running</em> objects, so the
    /// user watches live values rather than a frozen editor copy. Stopping puts them back.
    /// </summary>
    [Fact]
    public void WhilePlaying_ThePanelsResolveTheRunningObjects()
    {
        var editorRoot = GiveControllerAnOpenScene();
        Assert.Same(editorRoot, sceneTree.CurrentSceneRoot);
        Assert.False(sceneTree.IsShowingRuntimeScene);

        playMode.Start();

        Assert.True(sceneTree.IsShowingRuntimeScene);
        Assert.Same(playMode.PlayRoot, sceneTree.CurrentSceneRoot);
        Assert.Same(editorRoot, sceneTree.EditorSceneRoot);

        playMode.Stop();

        Assert.False(sceneTree.IsShowingRuntimeScene);
        Assert.Same(editorRoot, sceneTree.CurrentSceneRoot);
    }

    /// <summary>
    /// Selecting a node while playing must hand the Inspector the live instance whose values the
    /// game is mutating — not the identically-named node in the edited copy.
    /// </summary>
    [Fact]
    public void WhilePlaying_NodeLookupResolvesTheLiveInstance()
    {
        var editorRoot = GiveControllerAnOpenScene();
        var editorChild = editorRoot.Children[0];

        playMode.Start();
        playMode.Tick(0.016);

        var found = sceneTree.FindNodeById(editorChild.Id);

        Assert.NotNull(found);
        Assert.NotSame(editorChild, found);
        Assert.Same(playMode.PlayRoot!.Children[0], found);
        Assert.Equal(1f, found.Transform.Position.X);   // the live value the game just changed
        Assert.Equal(0f, editorChild.Transform.Position.X);
    }

    /// <summary>
    /// Tweaking a running object must not dirty the saved scene: those edits die with the session.
    /// </summary>
    [Fact]
    public void WhilePlaying_MarkingModifiedDoesNotDirtyTheSavedScene()
    {
        GiveControllerAnOpenScene();
        playMode.Start();

        sceneTree.MarkAssetModified();

        Assert.True(sceneTree.IsShowingRuntimeScene);
    }

    /// <summary>
    /// When game code loads a different scene mid-session, the panels follow it rather than keeping
    /// a stale hierarchy that is no longer running.
    /// </summary>
    [Fact]
    public void WhenGameCodeLoadsAnotherScene_ThePanelsFollowIt()
    {
        GiveControllerAnOpenScene();
        playMode.Start();
        var firstRoot = playMode.PlayRoot;

        // Stand in for a script calling SceneManager.LoadSceneAsync during play.
        var nextRoot = new Node { Name = "NextScene" };
        nextRoot.Awake(null);
        RuntimeServices.GetRequired<ISceneManager>().AdoptScene(Guid.NewGuid(), nextRoot);

        playMode.Tick(0.016);

        Assert.NotSame(firstRoot, playMode.PlayRoot);
        Assert.Same(nextRoot, playMode.PlayRoot);
        Assert.Same(nextRoot, sceneTree.CurrentSceneRoot);
    }

    /// <summary>
    /// The session's own clock drives <see cref="PlayModeService.Tick()"/>, so the caller does not
    /// have to track frame timing — and time spent paused is not replayed on resume.
    /// </summary>
    [Fact]
    public void Tick_UsesTheSessionClockAndDoesNotReplayPausedTime()
    {
        GiveControllerAnOpenScene();
        playMode.Start();

        playMode.Tick();
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        playMode.Pause();
        Thread.Sleep(50);
        playMode.Tick();
        Assert.Equal(1, FrameCountingComponent.TotalUpdates);

        playMode.Resume();
        playMode.Tick();

        Assert.Equal(2, FrameCountingComponent.TotalUpdates);
        Assert.Equal(2f, playMode.PlayRoot!.Children[0].Transform.Position.X);
    }

    /// <summary>Play mode publishes every transition so the toolbar can follow along.</summary>
    [Fact]
    public void StateChanged_ReportsEveryTransition()
    {
        GiveControllerAnOpenScene();
        var observed = new List<PlayState>();
        playMode.StateChanged += observed.Add;

        playMode.Start();
        playMode.Pause();
        playMode.Resume();
        playMode.Stop();

        Assert.Equal(
            [PlayState.Playing, PlayState.Paused, PlayState.Playing, PlayState.Stopped],
            observed);
    }

    /// <summary>
    /// While a session runs, gameplay resolves engine services from the play scope; stopping puts
    /// the editor's own provider back so the Scene View keeps working.
    /// </summary>
    [Fact]
    public void RuntimeServices_AreScopedToTheSessionAndRestoredOnStop()
    {
        var editorServices = new EmptyServiceProvider();
        var service = new PlayModeService(sceneTree, assetDatabase, editorServices, Log.Logger);
        GiveControllerAnOpenScene();

        service.Start();

        Assert.NotNull(RuntimeServices.TryGet<ISceneManager>());
        Assert.Same(service.Input, RuntimeServices.TryGet<IInputSource>());

        service.Stop();

        Assert.Null(RuntimeServices.TryGet<ISceneManager>());
    }

    /// <summary>
    /// A script that reassigns <see cref="CameraComponent.Priority"/> mid-session — a camera-cycling
    /// rig, for instance — must change which camera <see cref="PlayModeService.ActiveCamera"/>
    /// returns. This previously cached the first-resolved camera for the rest of the session
    /// (a <c>??=</c> guard), so every later priority change silently did nothing: the Game panel
    /// kept rendering whichever camera happened to be primary at the first frame, forever.
    /// </summary>
    [Fact]
    public void ActiveCamera_FollowsPriorityChangesMadeAfterStart()
    {
        var root = new Node { Name = "Root" };
        var nodeA = new Node { Name = "CamA" };
        nodeA.AddComponent<CameraComponent>().Priority = 10;
        var nodeB = new Node { Name = "CamB" };
        nodeB.AddComponent<CameraComponent>().Priority = 0;
        root.Children.Add(nodeA);
        root.Children.Add(nodeB);
        root.Awake(null);

        typeof(SceneTreeController)
            .GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);

        Assert.True(playMode.Start());

        var playRoot = playMode.PlayRoot ?? throw new InvalidOperationException("Play mode did not create a root node.");
        var runningCameras = Node.GetComponentsInChildren<CameraComponent>(playRoot).ToList();
        var runningA = runningCameras.Single(c => c.Node is { Name: "CamA" });
        var runningB = runningCameras.Single(c => c.Node is { Name: "CamB" });

        Assert.Same(runningA, playMode.ActiveCamera);

        runningA.Priority = 0;
        runningB.Priority = 10;

        Assert.Same(runningB, playMode.ActiveCamera);
    }

    /// <summary>
    /// Stopping evicts non-persistent <c>DataAsset</c> content from the editor's own
    /// <see cref="IAssetLoader"/> too — belt-and-braces alongside the session's own loader, which
    /// <see cref="PlayModeService.Stop"/> already discards outright by disposing the play scope.
    /// </summary>
    [Fact]
    public async Task Stop_EvictsNonPersistentDataFromTheEditorsOwnLoader()
    {
        var editorLoader = new RuntimeAssetLoader(assetDatabase);
        var session = new PlayModeService(sceneTree, assetDatabase, new SingleServiceProvider(editorLoader), Log.Logger);

        var projectRoot = Path.Combine(Path.GetTempPath(), $"turian-playmode-data-{Guid.NewGuid():N}");
        var assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);
        try
        {
            var assetPath = Path.Combine(assetsRoot, "counter.dataasset");
            var meta = new DataAssetAsset { RelativePath = assetPath };
            Serializer.Save(assetPath, new DataAssetTest { Int = 1 });
            Serializer.Save($"{assetPath}.meta", meta);
            Assert.True(assetDatabase.RegisterAsset(meta));

            var loaded = (DataAssetTest)(await editorLoader.LoadDataAsync(meta.Id))!;
            loaded.Int = 999; // some editor-side tool mutating shared state through the editor's own loader

            var root = new Node { Name = "Root" };
            root.Awake(null);
            typeof(SceneTreeController)
                .GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sceneTree, root);

            Assert.True(session.Start());
            session.Stop();

            var afterStop = (DataAssetTest)(await editorLoader.LoadDataAsync(meta.Id))!;
            Assert.Equal(1, afterStop.Int);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Resolves exactly one service, the way a real container would for the one type registered.</summary>
    sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }
}
