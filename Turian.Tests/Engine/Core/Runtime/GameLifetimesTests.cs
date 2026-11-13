namespace Turian.Tests;

/// <summary>Qualifies how a running game's lifetimes follow scene loads, restarts, injection and the ticker.</summary>
[Collection(SerialTests.Name)]
public sealed class GameLifetimesTests : IDisposable
{
    sealed class Progress : ISubsystem
    {
        public int Score { get; set; }
        public void Start() { }
        public void Stop() { }
    }

    sealed class Level : ISubsystem
    {
        public static int Stopped { get; set; }
        public void Start() { }
        public void Stop() => Stopped++;
    }

    sealed class World : ISubsystem, IFixedTickSystem
    {
        public int FixedTicks { get; private set; }
        public int Presented { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void FixedTick(long tick, double deltaTime) => FixedTicks++;
        public void AfterFixedTicks(double alpha) => Presented++;
    }

    sealed class Painter : ITickingSubsystem
    {
        public SubsystemTickPhase TickPhase => SubsystemTickPhase.Frame;
        public int Frames { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void Tick(float deltaTime) => Frames++;
    }

    readonly SceneManager scenes = new(new AssetDatabase());
    readonly List<Guid?> reloads = [];
    readonly GameLifetimes lifetimes;

    /// <summary>Binds a session with progress and a world, and a scene scope with a level, to a scene manager.</summary>
    public GameLifetimesTests()
    {
        Level.Stopped = 0;
        var registry = new SubsystemRegistry([
            SubsystemDescriptor.Of(LifetimeScope.Session, static _ => new Progress()),
            SubsystemDescriptor.Of(LifetimeScope.Session, static _ => new World()),
            SubsystemDescriptor.Of(LifetimeScope.Application, static _ => new Painter()),
            SubsystemDescriptor.Of(LifetimeScope.Scene, static _ => new Level()),
        ]);
        lifetimes = new GameLifetimes(new LifetimeHost(registry), scenes, id =>
        {
            reloads.Add(id);
            return Task.CompletedTask;
        });
        lifetimes.Start();
    }

    /// <inheritdoc />
    public void Dispose() => lifetimes.Dispose();

    Node AdoptScene(string name)
    {
        var root = new Node { Name = name };
        root.Children.Add(new Node { Name = name + " child" });
        scenes.AdoptScene(Guid.NewGuid(), root, LoadSceneMode.Additive);
        return root;
    }

    /// <summary>A scene scope begins when its scene loads, resolves session subsystems, and ends at unload.</summary>
    [Fact]
    public void SceneScopesFollowSceneLoadsAndUnloads()
    {
        var root = AdoptScene("First");
        var other = AdoptScene("Second");

        var scope = lifetimes.SceneOf(root.Children[0]);
        Assert.NotNull(scope);
        Assert.NotSame(scope, lifetimes.SceneOf(other));
        Assert.Same(lifetimes.Session!.Get<Progress>(), scope!.Get<Progress>());
        Assert.NotNull(scope.Get<Level>());
        Assert.Null(lifetimes.SceneOf(new Node()));

        scenes.UnloadScene(scenes.LoadedScenes.First(scene => scene.RootNode == root));

        Assert.Equal(1, Level.Stopped);
        Assert.Null(lifetimes.SceneOf(root));
        Assert.NotNull(lifetimes.SceneOf(other));
    }

    /// <summary>A restart waits for the frame to end, then unloads every scene and opens a fresh session.</summary>
    [Fact]
    public void RestartRecreatesTheSessionAfterTheFrame()
    {
        AdoptScene("Level");
        var progress = lifetimes.Session!.Get<Progress>();
        progress.Score = 7;
        var next = Guid.NewGuid();

        lifetimes.Restart(next);
        Assert.Same(progress, lifetimes.Session!.Get<Progress>());
        lifetimes.ApplyPendingRestart();

        Assert.Empty(scenes.LoadedScenes);
        Assert.Equal(1, Level.Stopped);
        Assert.NotSame(progress, lifetimes.Session!.Get<Progress>());
        Assert.Equal(0, lifetimes.Session!.Get<Progress>().Score);
        Assert.Equal([next], reloads);

        lifetimes.ApplyPendingRestart();
        Assert.Single(reloads);
    }

    /// <summary>Injection hands out the live session's subsystem, and other types come from the composed services.</summary>
    [Fact]
    public void InjectionResolvesTheLiveSessionFirst()
    {
        var stale = new Progress();
        var composed = new ServiceCollection().AddSingleton(stale).AddSingleton(scenes).BuildServiceProvider();
        var services = new SubsystemServiceProvider(composed, lifetimes.Host);

        var first = services.GetRequiredService<Progress>();
        Assert.Same(lifetimes.Session!.Get<Progress>(), first);
        Assert.Same(scenes, services.GetRequiredService<SceneManager>());

        lifetimes.Restart();
        lifetimes.ApplyPendingRestart();

        Assert.NotSame(first, services.GetRequiredService<Progress>());
        Assert.NotSame(stale, services.GetRequiredService<Progress>());
        Assert.Equal([null], reloads);
    }

    /// <summary>The ticker steps fixed-tick and frame subsystems and applies a restart once the frame ends.</summary>
    [Fact]
    public void TheTickerDrivesTheLifetimes()
    {
        var ticker = new SceneTicker(scenes, new SimulationClock()) { Lifetimes = lifetimes };
        var world = lifetimes.Session!.Get<World>();
        var painter = lifetimes.Host.Application.Get<Painter>();

        ticker.StepFrame();
        ticker.Tick(ticker.Clock.FixedDeltaTime * 2);

        Assert.True(world.FixedTicks >= 2);
        Assert.Equal(2, world.Presented);
        Assert.Equal(2, painter.Frames);

        lifetimes.Restart();
        ticker.StepFrame();

        Assert.NotSame(world, lifetimes.Session!.Get<World>());
        Assert.Same(painter, lifetimes.Host.Application.Get<Painter>());
        Assert.Single(reloads);
    }

    sealed class Stubborn : ISubsystem, IDisposable
    {
        public static int Disposed { get; set; }
        public void Start() { }
        public void Stop() => throw new InvalidOperationException("Stop failed.");
        public void Dispose()
        {
            Disposed++;
            throw new InvalidOperationException("Dispose failed.");
        }
    }

    /// <summary>A subsystem that fails to stop is still disposed, the rest still stop, and every failure is reported.</summary>
    [Fact]
    public void FailingSubsystemsStillEndTheirSession()
    {
        Stubborn.Disposed = 0;
        var stubborn = new GameLifetimes(new LifetimeHost(new SubsystemRegistry([
            SubsystemDescriptor.Of(LifetimeScope.Session, static _ => new Stubborn()),
            SubsystemDescriptor.Of(LifetimeScope.Session, static _ => new Progress()),
        ])), new SceneManager(new AssetDatabase()), static _ => Task.CompletedTask);
        stubborn.Start();

        var failure = Assert.Throws<AggregateException>(stubborn.Dispose);

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, Stubborn.Disposed);
        Assert.Null(stubborn.Session);
    }

    /// <summary>Resolving a live subsystem allocates nothing once the lifetimes run.</summary>
    [Fact]
    public void ResolutionDoesNotAllocate()
    {
        var root = AdoptScene("Level");
        var scope = lifetimes.SceneOf(root)!;
        _ = scope.Get<Progress>();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = scope.Get<Progress>();
            _ = lifetimes.SceneOf(root);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
