namespace Turian.Tests;

/// <summary>
/// Qualifies the game-loop phase contract: phase order, catch-up and its cap, presentation alpha, both destroy flushes,
/// run order across lifetimes and the ordering-only <see cref="SubsystemDescriptor.RunsAfter"/> edges.
/// </summary>
public sealed class GameLoopPhaseTests
{
    const double Fixed = 1d / 60d;
    const GameLoopPhases AllPhases = (GameLoopPhases)0xFF;

    abstract class Recorder(List<string> log, string name, GameLoopPhases phases = AllPhases) : IGameLoopSubsystem
    {
        public Dictionary<GameLoopPhase, LoopTime> Last { get; } = [];
        public GameLoopPhases Phases => phases;
        public void Start() => log.Add($"{name}:start");
        public void Stop() { }

        public void Run(GameLoopPhase phase, in LoopTime time)
        {
            Last[phase] = time;
            log.Add(phase == GameLoopPhase.AfterFixedTicks
                ? $"{name}:{phase}:{time.Tick}:{time.Alpha:F2}"
                : $"{name}:{phase}:{time.Tick}");
        }
    }

    sealed class First(List<string> log, GameLoopPhases phases = AllPhases) : Recorder(log, "first", phases);
    sealed class Second(List<string> log, GameLoopPhases phases = AllPhases) : Recorder(log, "second", phases);
    sealed class Third(List<string> log, GameLoopPhases phases = AllPhases) : Recorder(log, "third", phases);
    sealed class Fourth(List<string> log, GameLoopPhases phases = AllPhases) : Recorder(log, "fourth", phases);
    sealed class Missing : ISubsystem
    {
        public void Start() { }
        public void Stop() { }
    }

    [TypeId("b9813794-7438-4f35-b51b-b9d6b15e533d")]
    sealed class Probe(List<string> log) : Component
    {
        public override void OnFixedUpdate(float fixedDeltaTime) => log.Add("component:OnFixedUpdate");
        public override void OnUpdate(float deltaTime) => log.Add("component:OnUpdate");
        public override void OnLateUpdate(float deltaTime) => log.Add("component:OnLateUpdate");
    }

    sealed class Loop : IDisposable
    {
        public Loop(TimeSettings? time, params SubsystemDescriptor[] descriptors)
        {
            Scenes = new SceneManager(new AssetDatabase());
            Lifetimes = new GameLifetimes(new LifetimeHost(new SubsystemRegistry(descriptors)), Scenes,
                static _ => Task.CompletedTask);
            Lifetimes.Start();
            Ticker = new SceneTicker(Scenes, new SimulationClock(time)) { Lifetimes = Lifetimes };
        }

        public SceneManager Scenes { get; }
        public GameLifetimes Lifetimes { get; }
        public SceneTicker Ticker { get; }

        public Node Adopt(params Component[] components) => Adopt(null, components);

        public Node Adopt(Node? child, params Component[] components)
        {
            var root = new Node { Name = "Root" };
            foreach (var component in components)
            {
                root.AddComponent(component);
            }

            if (child is not null)
            {
                root.Children.Add(child);
            }

            root.Awake(null);
            Scenes.AdoptScene(Guid.NewGuid(), root);
            return root;
        }

        public void Dispose() => Lifetimes.Dispose();
    }

    static SubsystemDescriptor App<T>(Func<SubsystemScope, T> create) where T : class, ISubsystem =>
        SubsystemDescriptor.Of(LifetimeScope.Application, create);

    /// <summary>
    /// A frame begins, runs each fixed tick as components then FixedTick, FixedCommit and TickCompleted, then presents
    /// once: AfterFixedTicks, update, late update and EndFrame, each component pass ahead of its subsystem phase.
    /// </summary>
    [Fact]
    public void AFrameRunsThePhasesInOrder()
    {
        var log = new List<string>();
        using var loop = new Loop(null, App(_ => new First(log)));
        loop.Adopt(new Probe(log));
        log.Clear();

        loop.Ticker.Tick(Fixed * 2);

        Assert.Equal([
            "first:BeginFrame:0",
            "component:OnFixedUpdate", "first:FixedTick:1", "first:FixedCommit:1", "first:TickCompleted:1",
            "component:OnFixedUpdate", "first:FixedTick:2", "first:FixedCommit:2", "first:TickCompleted:2",
            "first:AfterFixedTicks:2:0.00",
            "component:OnUpdate", "first:Update:2",
            "component:OnLateUpdate", "first:LateUpdate:2",
            "first:EndFrame:2",
        ], log);
    }

    /// <summary>
    /// A frame shorter than a tick runs no fixed phase, but still presents once with the partial tick as alpha;
    /// the accumulated time runs the tick on a later frame.
    /// </summary>
    [Fact]
    public void AFrameWithoutATickStillPresents()
    {
        var log = new List<string>();
        using var loop = new Loop(null, App(_ => new First(log)));

        loop.Ticker.Tick(Fixed / 2);
        Assert.Equal(["first:BeginFrame:0", "first:AfterFixedTicks:0:0.50", "first:Update:0", "first:LateUpdate:0",
            "first:EndFrame:0"], log.Skip(1));

        log.Clear();
        loop.Ticker.Tick(Fixed / 2);
        Assert.Contains("first:TickCompleted:1", log);
        Assert.Contains("first:AfterFixedTicks:1:0.00", log);
    }

    /// <summary>
    /// TickCompleted runs once per tick, catch-up ticks included, never once per frame; the cap bounds the ticks of a
    /// frame and the retained backlog presents at alpha one and runs on the next frames.
    /// </summary>
    [Fact]
    public void CatchUpIsCappedAndBacklogIsRetained()
    {
        var log = new List<string>();
        using var loop = new Loop(new TimeSettings { MaxTicksPerFrame = 2 }, App(_ => new First(log)));

        loop.Ticker.Tick(Fixed * 5.5);
        Assert.Equal(2, log.Count(static entry => entry.StartsWith("first:TickCompleted", StringComparison.Ordinal)));
        Assert.Contains("first:AfterFixedTicks:2:1.00", log);

        log.Clear();
        loop.Ticker.Tick(0);
        Assert.Equal(["first:TickCompleted:3", "first:TickCompleted:4"],
            log.Where(static entry => entry.StartsWith("first:TickCompleted", StringComparison.Ordinal)));
    }

    /// <summary>With dropped backlog, a capped frame keeps only its partial tick, which alpha presents.</summary>
    [Fact]
    public void DroppedBacklogKeepsThePartialTickForInterpolation()
    {
        var log = new List<string>();
        using var loop = new Loop(new TimeSettings { MaxTicksPerFrame = 2, DropBacklog = true },
            App(_ => new First(log)));

        loop.Ticker.Tick(Fixed * 5.5);
        Assert.Contains("first:AfterFixedTicks:2:0.50", log);

        log.Clear();
        loop.Ticker.Tick(0);
        Assert.DoesNotContain(log, static entry => entry.Contains("TickCompleted", StringComparison.Ordinal));
    }

    /// <summary>
    /// A paused clock runs no tick, but every frame phase still runs with a zero delta; a frame step runs exactly one
    /// tick through every phase regardless.
    /// </summary>
    [Fact]
    public void APausedClockStillRunsTheFramePhases()
    {
        var log = new List<string>();
        var recorder = new First(log);
        using var loop = new Loop(null, App(_ => recorder));
        loop.Ticker.Clock.IsPaused = true;
        log.Clear();

        loop.Ticker.Tick(Fixed * 3);
        Assert.Equal(["first:BeginFrame:0", "first:AfterFixedTicks:0:0.00", "first:Update:0", "first:LateUpdate:0",
            "first:EndFrame:0"], log);
        Assert.Equal(0d, recorder.Last[GameLoopPhase.Update].DeltaTime);
        Assert.Equal(Fixed * 3, recorder.Last[GameLoopPhase.Update].UnscaledDeltaTime, 12);

        log.Clear();
        loop.Ticker.StepFrame();
        Assert.Single(log, static entry => entry == "first:TickCompleted:1");
    }

    [TypeId("c74a66ef-5a02-4e66-98e2-2b71d18ffeea")]
    sealed class DestroyedOnFixedTick : Component
    {
        public int FixedUpdates { get; private set; }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            FixedUpdates++;
            Node!.Destroy();
        }
    }

    /// <summary>
    /// Destruction requested in a fixed tick leaves with that tick, before the next catch-up tick, so how many ticks
    /// a frame catches up cannot change how long a destroyed node keeps simulating. This is the FixedCommit flush.
    /// </summary>
    [Fact]
    public void DestructionInAFixedTickLeavesBeforeTheNextTick()
    {
        using var loop = new Loop(null);
        var component = new DestroyedOnFixedTick();
        var child = new Node { Name = "Child" };
        child.AddComponent(component);
        loop.Adopt(child);

        loop.Ticker.Tick(Fixed * 3);

        Assert.Equal(1, component.FixedUpdates);
        Assert.True(child.IsDestroyed);
    }

    [TypeId("0d21f609-9b6a-4e21-9853-5065d82eb31d")]
    sealed class DestroyedOnUpdate : Component
    {
        public override void OnUpdate(float deltaTime) => Node!.Destroy();
    }

    sealed class EndFrameWitness(Node watched) : IGameLoopSubsystem
    {
        public bool? SawDestroyed { get; private set; }
        public GameLoopPhases Phases => GameLoopPhases.EndFrame;
        public void Start() { }
        public void Stop() { }
        public void Run(GameLoopPhase phase, in LoopTime time) => SawDestroyed = watched.IsDestroyed;
    }

    /// <summary>
    /// Destruction requested by the presentation phases leaves before EndFrame even when the frame ran no tick, so it
    /// cannot wait for a fixed-tick flush that a paused clock never runs. This is the end-of-frame flush.
    /// </summary>
    [Fact]
    public void DestructionInAnUpdateLeavesAtTheEndOfAFrameWithoutTicks()
    {
        var child = new Node { Name = "Child" };
        child.AddComponent(new DestroyedOnUpdate());
        var witness = new EndFrameWitness(child);
        using var loop = new Loop(null, App(_ => witness));
        loop.Adopt(child);
        loop.Ticker.Clock.IsPaused = true;

        loop.Ticker.Tick(Fixed);

        Assert.True(child.IsDestroyed);
        Assert.True(witness.SawDestroyed);
    }

    /// <summary>
    /// Within a phase the application's subsystems run first, then the session's, then each scene's; within a
    /// lifetime the run order puts a <see cref="SubsystemDescriptor.RunsAfter"/> target first and keeps registration
    /// order otherwise.
    /// </summary>
    [Fact]
    public void PhasesRunByLifetimeThenRunOrder()
    {
        var log = new List<string>();
        using var loop = new Loop(null,
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new Fourth(log, GameLoopPhases.Update)),
            SubsystemDescriptor.Of(LifetimeScope.Session, _ => new Third(log, GameLoopPhases.Update)),
            App(_ => new Second(log, GameLoopPhases.Update)).After(typeof(First)),
            App(_ => new First(log, GameLoopPhases.Update)));
        loop.Adopt();
        log.Clear();

        loop.Lifetimes.Host.Run(GameLoopPhase.Update, default);

        Assert.Equal(["first:Update:0", "second:Update:0", "third:Update:0", "fourth:Update:0"], log);
    }

    /// <summary>
    /// A run ordering is only an ordering: it does not change the start order, does not require, create or expose its
    /// target, and a missing target is ignored.
    /// </summary>
    [Fact]
    public void RunsAfterIsOrderingOnly()
    {
        var log = new List<string>();
        var registry = new SubsystemRegistry([
            App(_ => new Second(log, GameLoopPhases.Update)).After(typeof(First), typeof(Missing)),
            App(_ => new First(log, GameLoopPhases.Update)),
        ]);
        Assert.Equal([typeof(Second), typeof(First)], registry.For(LifetimeScope.Application).Select(static d => d.Type));
        Assert.Equal([typeof(First), typeof(Second)], registry.RunOrder(LifetimeScope.Application).Select(static d => d.Type));

        using var host = new LifetimeHost(registry);
        host.Start();
        Assert.Equal(["second:start", "first:start"], log);
        Assert.False(host.Application.TryGet<Missing>(out _));

        var alone = new SubsystemRegistry([App(_ => new Second(log)).After(typeof(First))]);
        Assert.Equal([typeof(Second)], alone.RunOrder(LifetimeScope.Application).Select(static d => d.Type));
    }

    /// <summary>
    /// Running after a longer-lived subsystem is allowed and changes nothing; running after a shorter-lived one, or in
    /// a cycle, is rejected.
    /// </summary>
    [Fact]
    public void RunsAfterRejectsWhatCannotBeHonoured()
    {
        var log = new List<string>();
        _ = new SubsystemRegistry([
            SubsystemDescriptor.Of(LifetimeScope.Session, _ => new Second(log)).After(typeof(First)),
            App(_ => new First(log)),
        ]);

        Assert.Contains("shorter-lived", Assert.Throws<InvalidOperationException>(() => new SubsystemRegistry([
            App(_ => new First(log)).After(typeof(Second)),
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new Second(log)),
        ])).Message);
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => new SubsystemRegistry([
            App(_ => new First(log)).After(typeof(Second)),
            App(_ => new Second(log)).After(typeof(First)),
        ])).Message);
    }

    sealed class Physics : ISubsystem, IFixedTickSystem
    {
        public List<string> Calls { get; } = [];
        public void Start() { }
        public void Stop() { }
        public void FixedTick(long tick, double deltaTime) => Calls.Add($"fixed:{tick}");
        public void AfterFixedTicks(double alpha) => Calls.Add("present");
    }

    sealed class Painter : ITickingSubsystem
    {
        public int Frames { get; private set; }
        public SubsystemTickPhase TickPhase => SubsystemTickPhase.Frame;
        public void Start() { }
        public void Stop() { }
        public void Tick(float deltaTime) => Frames++;
    }

    sealed class Both : IGameLoopSubsystem, IFixedTickSystem
    {
        public List<string> Calls { get; } = [];
        public GameLoopPhases Phases => GameLoopPhases.Update;
        public void Start() { }
        public void Stop() { }
        public void Run(GameLoopPhase phase, in LoopTime time) => Calls.Add(phase.ToString());
        public void FixedTick(long tick, double deltaTime) => Calls.Add("fixed");
        public void AfterFixedTicks(double alpha) => Calls.Add("present");
    }

    /// <summary>
    /// The earlier interfaces map onto phases unchanged, and a subsystem that also implements
    /// <see cref="IGameLoopSubsystem"/> is driven only through it.
    /// </summary>
    [Fact]
    public void EarlierSubsystemInterfacesMapOntoPhases()
    {
        var physics = new Physics();
        var painter = new Painter();
        var both = new Both();
        using var loop = new Loop(null, App(_ => physics), App(_ => painter), App(_ => both));

        loop.Ticker.Tick(Fixed * 2);

        Assert.Equal(["fixed:1", "fixed:2", "present"], physics.Calls);
        Assert.Equal(1, painter.Frames);
        Assert.Equal(["Update"], both.Calls);
    }

    /// <summary>
    /// The simulation loop commits each authoritative tick in FixedCommit and publishes it in TickCompleted, once per
    /// catch-up tick, on the game loop's clock; a session on another clock is refused when the loop starts.
    /// </summary>
    [Fact]
    public void TheSimulationLoopCommitsAndPublishesEveryTick()
    {
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = 0.01 });
        var world = new SimulationTestWorld();
        var session = new SimulationSession(world, clock, 42, "test-v1");
        var completed = 0;
        session.TickCompleted += _ => completed++;
        using var services = Services(clock, session);
        using var lifetimes = Lifetimes(services);
        var ticker = SceneTicker.Compose(services, lifetimes);
        session.Schedule(Guid.NewGuid(), 1, "add", JsonSerializer.SerializeToElement(new { amount = 5L }));

        ticker.Tick(0.02);

        Assert.Same(clock, ticker.Clock);
        Assert.Equal(2, clock.TickIndex);
        Assert.Single(world.Applied);
        Assert.Equal(2, completed);

        var otherClock = new SimulationSession(new SimulationTestWorld(), new SimulationClock(), 1, "test-v1");
        using var wrong = Services(clock, otherClock);
        Assert.Throws<InvalidOperationException>(() => Lifetimes(wrong).Dispose());
    }

    static ServiceProvider Services(SimulationClock clock, SimulationSession session) => new ServiceCollection()
        .AddSingleton(clock)
        .AddSingleton(session)
        .AddSingleton<ISceneManager>(new SceneManager(new AssetDatabase()))
        .AddSingleton<GameLoopScheduler>()
        .AddSimulationLoop()
        .BuildServiceProvider();

    static GameLifetimes Lifetimes(ServiceProvider services)
    {
        var host = new LifetimeHost(new SubsystemRegistry(services.GetServices<SubsystemDescriptor>()), services);
        var lifetimes = new GameLifetimes(host, (SceneManager)services.GetRequiredService<ISceneManager>(),
            static _ => Task.CompletedTask);
        lifetimes.Start();
        return lifetimes;
    }

    /// <summary>Input actions register once, read at the start of each frame, and run in no phase without actions.</summary>
    [Fact]
    public void InputActionsAreReadAtTheStartOfTheFrame()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IInputSource>(new BufferedInputSource())
            .AddInputActions()
            .BuildServiceProvider();

        var descriptor = Assert.Single(services.GetServices<SubsystemDescriptor>());
        Assert.Equal(LifetimeScope.Application, descriptor.Scope);
        Assert.Equal(GameLoopPhases.BeginFrame, new InputActionsLoop(services.GetRequiredService<InputActionService>()).Phases);
        Assert.Same(services.GetRequiredService<InputActionService>(), services.GetRequiredService<IInputActions>());
        Assert.Equal(GameLoopPhases.None, new InputActionsLoop(null).Phases);
    }
}
