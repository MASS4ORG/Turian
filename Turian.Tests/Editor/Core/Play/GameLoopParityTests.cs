using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>
/// The exported runtime, the Studio's play mode and the CLI's <c>playmode</c> run the identical game-loop contract:
/// for the same frame deltas they record the same phases, ticks, deltas and alphas. The one deliberate difference is
/// pause, which this fixture pins down too.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class GameLoopParityTests : IDisposable
{
    const double Fixed = 1d / 60d;

    /// <summary>Where the components and subsystem of the running host record; one host runs at a time.</summary>
    static List<string> trace = [];

    [TypeId("e727b2f0-5d23-417f-a8f4-ab883d906626")]
    sealed class TraceProbe : Component
    {
        public override void OnFixedUpdate(float fixedDeltaTime) => trace.Add($"OnFixedUpdate dt{fixedDeltaTime:F6}");
        public override void OnUpdate(float deltaTime) => trace.Add($"OnUpdate dt{deltaTime:F6}");
        public override void OnLateUpdate(float deltaTime) => trace.Add($"OnLateUpdate dt{deltaTime:F6}");
    }

    sealed class TraceSubsystem : IGameLoopSubsystem
    {
        public GameLoopPhases Phases => (GameLoopPhases)0xFF;
        public void Start() { }
        public void Stop() { }

        public void Run(GameLoopPhase phase, in LoopTime time) =>
            trace.Add($"{phase} t{time.Tick} dt{time.DeltaTime:F6} unscaled{time.UnscaledDeltaTime:F6} a{time.Alpha:F3}");
    }

    static void AddTrace(IServiceCollection services) =>
        services.AddSubsystem(LifetimeScope.Application, static _ => new TraceSubsystem());

    static Node Scene()
    {
        var root = new Node { Name = "Scene" };
        root.AddComponent(new TraceProbe());
        root.Awake(null);
        return root;
    }

    readonly string project = Directory.CreateTempSubdirectory("turian-loop-parity-").FullName;

    /// <summary>Creates an empty project for the exported runtime.</summary>
    public GameLoopParityTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    /// <inheritdoc />
    public void Dispose()
    {
        trace = [];
        Directory.Delete(project, recursive: true);
    }

    // A tick, a catch-up frame, two half-tick frames (one without a tick), and a frame past the eight-tick cap.
    static readonly double[] Frames = [Fixed, Fixed * 3, Fixed / 2, Fixed / 2, Fixed * 20];

    sealed class Runtime : IDisposable
    {
        readonly GameRuntime runtime;

        public Runtime(string project)
        {
            runtime = GameRuntime.Start(new GamePlayerContext(project, NullLogger.Instance,
                static services => services.BuildServiceProvider()), new BufferedInputSource(), AddTrace);
            runtime.Scenes.AdoptScene(Guid.NewGuid(), Scene());
        }

        public SceneTicker Ticker => runtime.Ticker;

        public void Dispose() => runtime.Dispose();
    }

    static PlayModeService Play()
    {
        var host = Substitute.For<IPlaySceneHost>();
        host.CurrentSceneRoot.Returns(Scene());
        var play = new PlayModeService(host, new AssetDatabase(), new ServiceCollection().BuildServiceProvider(),
            NullLogger.Instance)
        { ConfigureServices = AddTrace };
        Assert.True(play.Start());
        return play;
    }

    static List<string> Record(Action run)
    {
        trace = [];
        run();
        var recorded = trace;
        trace = [];
        return recorded;
    }

    /// <summary>
    /// For the same frame deltas, including catch-up, a frame without a tick, the cap and a frame step, the exported
    /// runtime and play mode record identical phases.
    /// </summary>
    [Fact]
    public void TheRuntimeAndPlayModeRunTheSamePhases()
    {
        var exported = Record(() =>
        {
            using var runtime = new Runtime(project);
            foreach (var frame in Frames)
            {
                runtime.Ticker.Tick(frame);
            }

            runtime.Ticker.StepFrame();
        });

        var played = Record(() =>
        {
            var play = Play();
            try
            {
                foreach (var frame in Frames)
                {
                    play.Tick(frame);
                }

                play.Pause();
                play.StepFrame();
            }
            finally
            {
                play.Stop();
            }
        });

        // 1 + 3 + 0 + 1 + 8 (capped, backlog retained) ticks, then the frame step's one.
        Assert.Contains(exported, static entry => entry.StartsWith("TickCompleted t14 ", StringComparison.Ordinal));
        Assert.Contains(exported, static entry => entry.StartsWith("AfterFixedTicks t13 ", StringComparison.Ordinal)
            && entry.EndsWith("a1.000", StringComparison.Ordinal));
        Assert.Equal(exported, played);
    }

    /// <summary>
    /// The CLI's default frame is exactly one fixed step, so it records what the runtime records for one fixed
    /// interval per frame: N frames are N ticks on any machine.
    /// </summary>
    [Fact]
    public void TheCliRunsOneFixedStepPerFrameLikeTheRuntime()
    {
        const int frames = 5;
        var exported = Record(() =>
        {
            using var runtime = new Runtime(project);
            for (var i = 0; i < frames; i++)
            {
                runtime.Ticker.Tick(runtime.Ticker.Clock.FixedDeltaTime);
            }
        });

        var cli = Record(() =>
        {
            var play = Play();
            try
            {
                for (var i = 0; i < frames; i++)
                {
                    Assert.Equal(play.FixedDeltaTime, HeadlessPlay.TickFrame(play, realtime: false));
                }
            }
            finally
            {
                play.Stop();
            }
        });

        Assert.Equal(frames, cli.Count(static entry => entry.StartsWith("TickCompleted ", StringComparison.Ordinal)));
        Assert.Equal(exported, cli);
    }

    /// <summary>
    /// Pause differs on purpose. A running game's paused clock still runs every frame phase with a zero delta, so
    /// input, UI and presentation stay live; the Studio's pause skips the frame entirely, so the session is inspected
    /// exactly as it stopped. A frame step is identical in both.
    /// </summary>
    [Fact]
    public void PauseIsDeliberatelyAsymmetric()
    {
        var exported = Record(() =>
        {
            using var runtime = new Runtime(project);
            runtime.Ticker.Tick(Fixed);
            trace.Clear();
            runtime.Ticker.Clock.IsPaused = true;
            runtime.Ticker.Tick(Fixed * 2);
        });

        Assert.Equal([
            $"BeginFrame t1 dt{0d:F6} unscaled{Fixed * 2:F6} a0.000",
            $"AfterFixedTicks t1 dt{0d:F6} unscaled{Fixed * 2:F6} a0.000",
            $"OnUpdate dt{0d:F6}",
            $"Update t1 dt{0d:F6} unscaled{Fixed * 2:F6} a0.000",
            $"OnLateUpdate dt{0d:F6}",
            $"LateUpdate t1 dt{0d:F6} unscaled{Fixed * 2:F6} a0.000",
            $"EndFrame t1 dt{0d:F6} unscaled{Fixed * 2:F6} a0.000",
        ], exported);

        var play = Play();
        try
        {
            play.Tick(Fixed);
            play.Pause();
            Assert.Empty(Record(() => play.Tick(Fixed * 2)));

            var stepped = Record(play.StepFrame);
            Assert.Single(stepped, static entry => entry.StartsWith("TickCompleted t2 ", StringComparison.Ordinal));
            Assert.Contains(stepped, static entry => entry.StartsWith("EndFrame t2 ", StringComparison.Ordinal));
        }
        finally
        {
            play.Stop();
        }
    }
}
