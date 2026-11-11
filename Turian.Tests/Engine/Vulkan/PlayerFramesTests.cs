namespace Turian.Tests;

/// <summary>The windowed player's frame loop, driven without a window or GPU.</summary>
[Collection(SerialTests.Name)]
public sealed class PlayerFramesTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-player-frames-").FullName;
    readonly List<(ICamera Camera, Node Root)> rendered = [];
    readonly List<Texture?> overlays = [];
    Int2 framebuffer = new(320, 200);
    int closed;

    /// <summary>Starts every test outside a quit request.</summary>
    public PlayerFramesTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Application.BeginSession(headless: false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Application.BeginSession(headless: false);
        Directory.Delete(project, recursive: true);
    }

    GameRuntime Runtime() => GameRuntime.Start(
        new GamePlayerContext(project, NullLogger.Instance, static services => services.BuildServiceProvider()),
        new BufferedInputSource());

    PlayerFrames Frames(GameRuntime runtime, IUiPresenter? presenter) => new(runtime, presenter, () => framebuffer,
        overlays.Add, (_, camera, root) => rendered.Add((camera, root)), () => closed++);

    static CameraComponent AddCamera(Node parent)
    {
        var camera = new CameraComponent();
        var node = new Node { Name = "Camera" };
        node.AddComponent(camera);
        node.Parent = parent;
        parent.Children.Add(node);
        return camera;
    }

    /// <summary>
    /// A frame ticks the game, composites the overlay rendered for the startup or active root, and renders every
    /// root that has a primary camera.
    /// </summary>
    [Fact]
    public void FramesTickCompositeAndRenderEachCameraRoot()
    {
        var runtime = Runtime();
        var camera = AddCamera(runtime.Scenes.PersistentRoot);
        var presenter = Substitute.For<IUiPresenter>();

        Frames(runtime, presenter).Frame(0.5);

        Assert.Equal(0.5, runtime.Ticker.UnscaledElapsedSeconds);
        presenter.Received(1).TryRenderOverlay(runtime.Scenes.PersistentRoot, 320, 200, 0.5f);
        Assert.Single(overlays);
        var (renderedCamera, renderedRoot) = Assert.Single(rendered);
        Assert.Same(camera, renderedCamera);
        Assert.Same(runtime.Scenes.PersistentRoot, renderedRoot);
        Assert.Equal(0, closed);
    }

    sealed class PauseRecorder : IEngineSessionParticipant, IEngineSessionPauseParticipant
    {
        public List<bool> Changes { get; } = [];
        public void Start() { }
        public void Stop() { }
        public void SetPaused(bool paused) => Changes.Add(paused);
    }

    /// <summary>Session features hear once when the application leaves the foreground and once when it returns.</summary>
    [Fact]
    public void SessionFeaturesFollowTheForeground()
    {
        var recorder = new PauseRecorder();
        var runtime = GameRuntime.StartClient(
            new GamePlayerContext(project, NullLogger.Instance, static services => services.BuildServiceProvider()),
            new BufferedInputSource(),
            services => services.AddSingleton<IEngineSessionParticipant>(recorder));
        var frames = Frames(runtime, null);

        frames.Paused = false;
        frames.Paused = true;
        frames.Paused = true;
        frames.Paused = false;

        Assert.Equal([true, false], recorder.Changes);
    }

    /// <summary>A player that owns its window opens an interactive client session on the input it supplies.</summary>
    [Fact]
    public void PlayersOwningTheirWindowOpenInteractiveClientSessions()
    {
        var input = new BufferedInputSource();

        var runtime = GameRuntime.StartClient(
            new GamePlayerContext(project, NullLogger.Instance, static services => services.BuildServiceProvider()), input);

        var session = runtime.Services.GetRequiredService<EngineSessionContext>();
        Assert.Equal(EngineSessionKind.Client, session.Kind);
        Assert.True(session.Interactive);
        Assert.Same(input, runtime.Services.GetRequiredService<IInputSource>());
    }

    /// <summary>Once another scene replaces the startup scene, the overlay is built from the scene now active.</summary>
    [Fact]
    public void OverlayFollowsTheActiveSceneAfterTheStartupSceneIsReplaced()
    {
        var runtime = Runtime();
        var path = Path.Combine(project, "Assets", "scene.prefab");
        Serializer.Save(path, new Node { Name = "Startup" });
        var prefab = new Prefab { RelativePath = path };
        Assert.True(runtime.Assets.RegisterAsset(prefab));
        runtime.Settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(prefab.Id);
        runtime.LoadStartupScene();
        var presenter = Substitute.For<IUiPresenter>();
        var frames = Frames(runtime, presenter);

        frames.Frame(0.1);
        presenter.Received(1).TryRenderOverlay(runtime.StartupRoot!, 320, 200, 0.1f);

        var replacement = new Node { Name = "Next" };
        runtime.Scenes.AdoptScene(Guid.NewGuid(), replacement);
        frames.Frame(0.1);
        presenter.Received(1).TryRenderOverlay(replacement, 320, 200, 0.1f);
    }

    /// <summary>
    /// No overlay is drawn without a presenter or while the window is minimized; roots without a camera are skipped.
    /// </summary>
    [Fact]
    public void FramesSkipOverlaysAndCameralessRoots()
    {
        var runtime = Runtime();
        var presenter = Substitute.For<IUiPresenter>();
        framebuffer = new Int2(0, 0);

        Frames(runtime, presenter).Frame(0.1);
        Frames(runtime, null).Frame(0.1);

        presenter.DidNotReceiveWithAnyArgs().TryRenderOverlay(default!, default, default, default);
        Assert.Empty(overlays);
        Assert.Empty(rendered);
    }

    /// <summary>Once the game quits, the next frame closes the window instead of ticking.</summary>
    [Fact]
    public void QuittingClosesTheWindow()
    {
        var runtime = Runtime();
        PlayerFrames.KeyPressed(Key.Space);
        Assert.False(Application.QuitToken.IsCancellationRequested);

        PlayerFrames.KeyPressed(Key.Escape);
        Frames(runtime, null).Frame(0.1);

        Assert.Equal(1, closed);
        Assert.Equal(0, runtime.Ticker.UnscaledElapsedSeconds);
    }

    /// <summary>
    /// While the surface is lost frames neither tick nor render, the first frame after resuming ticks no time, and
    /// quitting still closes the window.
    /// </summary>
    [Fact]
    public void PausedFramesSkipTickingAndResumeWithoutTheGap()
    {
        var runtime = Runtime();
        AddCamera(runtime.Scenes.PersistentRoot);
        var frames = Frames(runtime, null);

        frames.Paused = true;
        frames.Frame(0.5);
        Assert.Empty(rendered);
        Assert.Equal(0, runtime.Ticker.UnscaledElapsedSeconds);

        frames.Paused = false;
        frames.Frame(30);
        Assert.Equal(0, runtime.Ticker.UnscaledElapsedSeconds);
        Assert.Single(rendered);

        frames.Frame(0.25);
        Assert.Equal(0.25, runtime.Ticker.UnscaledElapsedSeconds);

        frames.Paused = true;
        PlayerFrames.KeyPressed(Key.Escape);
        frames.Frame(0.1);
        Assert.Equal(1, closed);
    }

    /// <summary>Resizing gives the active scene's cameras the framebuffer's aspect, and does nothing without one.</summary>
    [Fact]
    public void ResizeUpdatesTheActiveScenesCameras()
    {
        var runtime = Runtime();
        var frames = Frames(runtime, null);
        frames.Resize(400, 200);

        var root = new Node { Name = "Scene" };
        var camera = AddCamera(root);
        runtime.Scenes.AdoptScene(Guid.NewGuid(), root);
        frames.Resize(400, 200);

        Assert.Equal(2f, camera.AspectRatio);
    }

    /// <summary>An interface package's presenter is created playing; without a package there is none.</summary>
    [Fact]
    public void PresentersAreCreatedPlaying()
    {
        var runtime = Runtime();
        var factory = Substitute.For<IUiPresenterFactory>();
        var presenter = Substitute.For<IUiPresenter>();
        factory.Create(null!, Arg.Any<IInputSource>(), Arg.Any<IServiceProvider>()).Returns(presenter);

        Assert.Null(PlayerFrames.CreatePresenter(null, null!, new BufferedInputSource(), runtime.Services));
        Assert.Same(presenter, PlayerFrames.CreatePresenter(factory, null!, new BufferedInputSource(), runtime.Services));
        Assert.True(presenter.IsPlaying);
    }

    /// <summary>A window icon is read from the PNG a build wrote, and is absent without one.</summary>
    [Fact]
    public void WindowIconsLoadFromTheBuiltPng()
    {
        var path = PlayerSettings.BuiltIconFileName;
        var content = new FileContentDirectory(project);
        Assert.Null(GlfwSurfaceHost.LoadIcon(content, path));

        using (var bitmap = new SKBitmap(4, 2))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            File.WriteAllBytes(Path.Combine(project, path), png.ToArray());
        }

        var icon = GlfwSurfaceHost.LoadIcon(content, path);
        Assert.Equal(4, icon!.Value.Width);
        Assert.Equal(2, icon.Value.Height);

        var window = Substitute.For<Silk.NET.Windowing.IWindow>();
        Assert.True(GlfwSurfaceHost.TryLoadIcon(window, content, path, out var target, out var loaded));
        Assert.Same(window, target);
        Assert.Equal(4, loaded.Width);
        Assert.False(GlfwSurfaceHost.TryLoadIcon(Substitute.For<Silk.NET.Windowing.IView>(), content, path, out _,
            out _));
        Assert.False(GlfwSurfaceHost.TryLoadIcon(window, content, "missing.png", out _, out _));
    }

    /// <summary>Disposing the frame loop unwinds gameplay before releasing the interface presenter.</summary>
    [Fact]
    public void DisposingReleasesThePresenter()
    {
        var presenter = Substitute.For<IUiPresenter>();
        var runtime = Runtime();
        var cleaned = false;
        runtime.Ticker.Async.Forget(Wait());
        presenter.When(value => value.Dispose()).Do(_ => Assert.True(cleaned));

        Frames(runtime, presenter).Dispose();
        Frames(Runtime(), null).Dispose();

        presenter.Received(1).Dispose();
        Assert.True(runtime.Ticker.Async.IsStopped);

        async ValueTask Wait()
        {
            try { await runtime.Ticker.Async.NextFrame(TestContext.Current.CancellationToken); }
            finally { cleaned = true; }
        }
    }
}
