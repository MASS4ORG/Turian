using Turian.Testing;

namespace Turian.Tests;

/// <summary>Checks headless frames, input, strict callback failures and scene cleanup.</summary>
[Collection(SerialTests.Name)]
public sealed class TestSceneTests
{
    sealed class Probe : Component
    {
        [InjectService, JsonIgnore] public IInputSource Input { get; private set; } = null!;
        public int Frames { get; private set; }
        public int FixedTicks { get; private set; }
        public int Presses { get; private set; }
        public bool Destroyed { get; private set; }
        public override void OnUpdate(float deltaTime)
        {
            Frames++;
            if (Input.WasKeyPressed(Key.Space)) { Presses++; }
        }
        public override void OnFixedUpdate(float deltaTime) => FixedTicks++;
        public override void OnDestroy() => Destroyed = true;
    }

    sealed class Broken : Component
    {
        public override void OnUpdate(float deltaTime) => throw new InvalidOperationException("callback failure");
    }

    sealed class SceneLoop : IGameLoopSubsystem
    {
        public bool Started { get; private set; }
        public int Frames { get; private set; }
        public GameLoopPhases Phases => GameLoopPhases.Update;
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Run(GameLoopPhase phase, in LoopTime time) => Frames++;
    }

    /// <summary>The harness owns a real scene scope and recreates its root after a session restart.</summary>
    [Fact]
    public void DrivesSceneSubsystemsAndSessionRestart()
    {
        var loop = new SceneLoop();
        using var scene = new TestScene(services => services.AddSubsystem(LifetimeScope.Scene, _ => loop));
        var lifetime = scene.Services.GetRequiredService<ISessionLifetime>();
        Assert.NotNull(lifetime.SceneOf(scene.Root));
        Assert.True(loop.Started);
        scene.Step();
        Assert.Equal(1, loop.Frames);
        var previous = scene.Root;
        lifetime.Restart();
        scene.Step();
        Assert.True(previous.IsDestroyed);
        Assert.NotSame(previous, scene.Root);
        Assert.NotNull(lifetime.SceneOf(scene.Root));
        scene.Dispose();
        Assert.False(loop.Started);
    }

    /// <summary>In-memory scenes also work without a service provider and reject empty names.</summary>
    [Fact]
    public void CreatesEmptyScenesWithoutFiles()
    {
        var manager = new SceneManager(new AssetDatabase());
        var scene = manager.CreateScene("Empty");
        Assert.Same(scene, manager.ActiveScene);
        Assert.Equal("Empty", scene.RootNode.Name);
        Assert.Throws<ArgumentException>(() => manager.CreateScene(" "));
        manager.UnloadAllScenes();
    }

    /// <summary>Components see injected scripted input, production frame edges and controlled elapsed time.</summary>
    [Fact]
    public void RunsFramesAndScriptedInput()
    {
        using var scene = new TestScene();
        var probe = scene.Add<Probe>();
        scene.Step(4, beforeFrame: static (frame, input) =>
        {
            if (frame == 0) { input.PushKeyDown(Key.Space); }
            if (frame == 2) { input.PushKeyUp(Key.Space); }
        });
        Assert.Same(probe, scene.Find<Probe>());
        Assert.Equal(4, probe.Frames);
        Assert.Equal(4, probe.FixedTicks);
        Assert.Equal(1, probe.Presses);
        Assert.Equal(4d / 60d, scene.Ticker.ElapsedSeconds, 8);
        Assert.Null(scene.Services.GetService<IRenderDevice>());
        Assert.NotNull(scene.Services.GetRequiredService<ISessionLifetime>().Session);
        Assert.NotNull(scene.Services.GetRequiredService<AssetDatabase>());
        Assert.Null(scene.Find<Broken>());
    }

    /// <summary>Variable frames use the production fixed accumulator, and empty steps do no work.</summary>
    [Fact]
    public void PreservesClockSemantics()
    {
        using var scene = new TestScene();
        var probe = scene.Add<Probe>();
        scene.Step(0);
        scene.Step(2, SceneTicker.FixedTimestep / 2);
        Assert.Equal(2, probe.Frames);
        Assert.Equal(1, probe.FixedTicks);
    }

    /// <summary>A strict scene fails on callback errors, while an ordinary ticker continues other components.</summary>
    [Fact]
    public void PropagatesComponentFailures()
    {
        using var scene = new TestScene();
        scene.Add<Broken>();
        Assert.Equal("callback failure", Assert.Throws<InvalidOperationException>(() => scene.Step()).Message);
        var probe = scene.Add<Probe>();
        var ticker = new SceneTicker(scene.Services.GetRequiredService<ISceneManager>()) { Async = scene.Ticker.Async };
        ticker.Tick(SceneTicker.FixedTimestep);
        ticker.Tick(SceneTicker.FixedTimestep);
        Assert.Equal(2, probe.Frames);
    }

    /// <summary>Disposal destroys components once, stops waits and rejects further stepping or additions.</summary>
    [Fact]
    public void DisposesItsHierarchyAndServices()
    {
        var scene = new TestScene();
        var probe = scene.Add<Probe>();
        scene.Dispose();
        scene.Dispose();
        Assert.True(probe.Destroyed);
        Assert.True(scene.Ticker.Async.IsStopped);
        Assert.Throws<ObjectDisposedException>(() => scene.Step());
        Assert.Throws<ObjectDisposedException>(() => scene.Add<Probe>());
    }

    /// <summary>Explicit services are available before component Awake.</summary>
    [Fact]
    public void AllowsExplicitServiceConfiguration()
    {
        var marker = new object();
        using var scene = new TestScene(services => services.AddSingleton(marker));
        Assert.Same(marker, scene.Services.GetService<object>());
    }

    /// <summary>Invalid step requests never advance a partial frame.</summary>
    [Theory]
    [InlineData(-1, 0.01)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    public void RejectsInvalidTime(int frames, double delta)
    {
        using var scene = new TestScene();
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.Step(frames, delta));
        Assert.Equal(0, scene.Ticker.Clock.TickIndex);
    }

    /// <summary>The assertion helpers work without a test-framework dependency.</summary>
    [Fact]
    public void AssertionsReportExpectations()
    {
        SceneAssert.True(true);
        SceneAssert.Equal(3, 3);
        var value = new object();
        Assert.Same(value, SceneAssert.NotNull(value));
        Assert.Throws<SceneAssertionException>(static () => SceneAssert.True(false, "expected"));
        Assert.Throws<SceneAssertionException>(static () => SceneAssert.Equal(3, 4));
        Assert.Throws<SceneAssertionException>(static () => SceneAssert.NotNull<object>(null));
    }
}
