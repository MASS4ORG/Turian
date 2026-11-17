using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Turian.Engine.Core;

namespace Turian.Testing;

/// <summary>Owns a headless scene with the engine's frame phases, injected services and scripted input.</summary>
public sealed class TestScene : IDisposable
{
    readonly ServiceProvider provider;
    readonly GameLifetimes lifetimes;
    readonly SceneManager scenes;
    bool disposed;

    /// <summary>Creates a scene without discovering platform, rendering or audio modules.</summary>
    /// <param name="configure">Optional explicit services and subsystems for the test.</param>
    public TestScene(Action<IServiceCollection>? configure = null)
    {
        var assets = new AssetDatabase();
        scenes = new SceneManager(assets);
        var clock = new SimulationClock();
        var scheduler = new GameLoopScheduler();
        var registrations = new ServiceCollection()
            .AddSingleton<IInputSource>(Input)
            .AddSingleton<ISceneManager>(scenes)
            .AddSingleton(assets)
            .AddSingleton<ISessionLifetime>(_ => lifetimes
                ?? throw new InvalidOperationException("The test session has not started."))
            .AddSingleton(clock)
            .AddSingleton(scheduler)
            .AddSingleton<IAppSettings>(new AppSettings())
            .AddSingleton<LayerFilter>()
            .AddInputActions()
            .AddSubsystem(LifetimeScope.Session, static _ => new DataAssetSession());
        configure?.Invoke(registrations);
        provider = registrations.BuildServiceProvider();
        var host = new LifetimeHost(new SubsystemRegistry(provider.GetServices<SubsystemDescriptor>()), provider);
        lifetimes = new GameLifetimes(host, scenes, _ =>
        {
            Root = scenes.CreateScene("Test Scene").RootNode;
            return Task.CompletedTask;
        });
        Services = new SubsystemServiceProvider(provider, host);
        try
        {
            lifetimes.Start();
            scenes.BindServices(Services);
            Root = scenes.CreateScene("Test Scene").RootNode;
            Ticker = new SceneTicker(scenes, clock)
            {
                Async = scheduler,
                Lifetimes = lifetimes,
                InputSource = Input,
                Systems = [.. provider.GetServices<IFixedTickSystem>()],
                ComponentException = static error => ExceptionDispatchInfo.Capture(error).Throw(),
            };
        }
        catch
        {
            lifetimes.Dispose();
            scheduler.Dispose();
            provider.Dispose();
            throw;
        }
    }

    /// <summary>The hierarchy owned by this test.</summary>
    public Node Root { get; private set; } = null!;

    /// <summary>The input buffer; push key, pointer, touch or gamepad events before a frame.</summary>
    public BufferedInputSource Input { get; } = new();

    /// <summary>The production scene driver, including its clock and gameplay scheduler.</summary>
    public SceneTicker Ticker { get; }

    /// <summary>The test's isolated service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Adds a component on its own node, using normal Awake, injection and Start semantics.</summary>
    public T Add<T>() where T : Component, new()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var node = new Node { Name = typeof(T).Name };
        node.Awake(Root, Services);
        return node.AddComponent<T>();
    }

    /// <summary>Finds the first component of a type in the test hierarchy.</summary>
    public T? Find<T>() where T : Component => Root.GetComponentsInChildren<T>().FirstOrDefault();

    /// <summary>Advances deterministic frames; an optional script pushes input before each frame.</summary>
    /// <param name="frames">How many frames to simulate.</param>
    /// <param name="deltaTime">Seconds per frame; defaults to the fixed interval.</param>
    /// <param name="beforeFrame">Receives the zero-based frame number and input buffer.</param>
    public void Step(int frames = 1, double? deltaTime = null, Action<int, BufferedInputSource>? beforeFrame = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        var delta = deltaTime ?? Ticker.Clock.FixedDeltaTime;
        if (!double.IsFinite(delta) || delta < 0) { throw new ArgumentOutOfRangeException(nameof(deltaTime)); }
        for (var frame = 0; frame < frames; frame++)
        {
            beforeFrame?.Invoke(frame, Input);
            Ticker.Tick(delta);
        }
    }

    /// <summary>Destroys the hierarchy, stops waits and releases services even after a failed test.</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        try
        {
            scenes.UnloadAllScenes();
            scenes.PersistentRoot.DestroyImmediate();
        }
        finally
        {
            try { lifetimes.Dispose(); }
            finally
            {
                Ticker.Async.Dispose();
                provider.Dispose();
            }
        }
    }
}
