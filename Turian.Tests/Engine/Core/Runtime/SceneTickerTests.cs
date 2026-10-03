namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="SceneTicker"/>, the shared game-loop driver used by both the standalone
/// runtime and the Studio's in-editor play mode.
/// </summary>
public class SceneTickerTests : IDisposable
{
    readonly AssetDatabase assetDatabase;

    /// <summary>Resets the <see cref="AssetDatabase"/> singleton, as the other suites do.</summary>
    public SceneTickerTests()
    {
        assetDatabase = new AssetDatabase();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [TypeId("a4000001-0000-4000-8000-000000000001")]
    sealed class CountingComponent : Component
    {
        public int StartCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int LateUpdateCount { get; private set; }
        public int FixedUpdateCount { get; private set; }
        /// <summary>The interval supplied to the most recent fixed callback.</summary>
        public float LastFixedDeltaTime { get; private set; }
        public int UpdateCountAtFirstStart { get; private set; } = -1;

        public override void OnStart()
        {
            StartCount++;
            UpdateCountAtFirstStart = UpdateCount;
        }

        public override void OnUpdate(float deltaTime) => UpdateCount++;
        public override void OnLateUpdate(float deltaTime) => LateUpdateCount++;
        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            FixedUpdateCount++;
            LastFixedDeltaTime = fixedDeltaTime;
        }
    }

    sealed class ClickerComponent : Component
    {
        float accumulated;

        [InjectService, JsonIgnore]
        public IInputSource? Input { get; private set; }

        public int Currency { get; private set; }

        public override void OnUpdate(float deltaTime)
        {
            if (Input?.WasKeyPressed(Key.Space) == true) Currency++;
            accumulated += deltaTime;
            while (accumulated >= 1f)
            {
                Currency++;
                accumulated -= 1f;
            }
        }
    }

    (SceneTicker Ticker, CountingComponent Component) CreateScene()
    {
        var sceneManager = new SceneManager(assetDatabase);

        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Child" };
        var component = new CountingComponent();
        child.AddComponent(component);
        root.Children.Add(child);
        root.Awake(null);

        sceneManager.AdoptScene(Guid.NewGuid(), root);

        return (new SceneTicker(sceneManager), component);
    }

    /// <summary>
    /// A tick longer than the fixed timestep runs as many fixed steps as fit, and exactly one
    /// variable and late pass.
    /// </summary>
    [Fact]
    public void Tick_RunsFixedStepsForAccumulatedTime()
    {
        var (ticker, component) = CreateScene();

        ticker.Tick(SceneTicker.FixedTimestep * 3);

        Assert.Equal(3, component.FixedUpdateCount);
        Assert.Equal(1, component.UpdateCount);
        Assert.Equal(1, component.LateUpdateCount);
    }

    /// <summary>
    /// Time shorter than the fixed timestep accumulates across ticks rather than being dropped.
    /// </summary>
    [Fact]
    public void Tick_AccumulatesShortFramesUntilAFixedStepFits()
    {
        var (ticker, component) = CreateScene();

        ticker.Tick(SceneTicker.FixedTimestep / 2);
        Assert.Equal(0, component.FixedUpdateCount);

        ticker.Tick(SceneTicker.FixedTimestep / 2);
        Assert.Equal(1, component.FixedUpdateCount);
    }

    /// <summary>
    /// <see cref="Component.OnStart"/> runs exactly once, before the component's first update.
    /// </summary>
    [Fact]
    public void Tick_StartsComponentsOnceBeforeTheFirstUpdate()
    {
        var (ticker, component) = CreateScene();

        ticker.Tick(0.016);
        ticker.Tick(0.016);

        Assert.Equal(1, component.StartCount);
        Assert.Equal(0, component.UpdateCountAtFirstStart);
        Assert.Equal(2, component.UpdateCount);
    }

    /// <summary>
    /// A frame step advances exactly one frame, regardless of how little time has accumulated.
    /// </summary>
    [Fact]
    public void StepFrame_AdvancesExactlyOneFrame()
    {
        var (ticker, component) = CreateScene();

        ticker.StepFrame();

        Assert.Equal(1, component.FixedUpdateCount);
        Assert.Equal(1, component.UpdateCount);
        Assert.Equal(1, component.LateUpdateCount);
    }

    /// <summary>Inactive nodes are skipped entirely.</summary>
    [Fact]
    public void Tick_SkipsInactiveNodes()
    {
        var (ticker, component) = CreateScene();
        var node = component.Node ?? throw new InvalidOperationException("Test component must be attached to a node.");
        node.IsActive = false;

        ticker.Tick(SceneTicker.FixedTimestep);

        Assert.Equal(0, component.UpdateCount);
        Assert.Equal(0, component.FixedUpdateCount);
    }

    /// <summary>The ticker ends each frame on the input source so per-frame edges do not leak.</summary>
    [Fact]
    public void Tick_AdvancesTheInputSourceFrame()
    {
        var sceneManager = new SceneManager(assetDatabase);
        var input = new BufferedInputSource();
        var ticker = new SceneTicker(sceneManager) { InputSource = input };

        input.PushKeyDown(Key.W);
        Assert.True(input.WasKeyPressed(Key.W));

        ticker.Tick(0.016);

        Assert.False(input.WasKeyPressed(Key.W));
        Assert.True(input.IsKeyDown(Key.W));
    }

    /// <summary>Clicks and simulated time can be advanced independently of a wall clock.</summary>
    [Fact]
    public void Tick_DrivesClickerWithSyntheticInputAndScaledTime()
    {
        var input = new BufferedInputSource();
        using var services = new ServiceCollection().AddSingleton<IInputSource>(input).BuildServiceProvider();
        var clicker = new ClickerComponent();
        var root = new Node();
        root.Components.Add(clicker);
        root.Awake(null, services);
        var sceneManager = new SceneManager(assetDatabase);
        sceneManager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(sceneManager) { InputSource = input };

        input.PushKeyDown(Key.Space);
        ticker.Tick(0.5);
        Assert.Equal(1, clicker.Currency);
        ticker.Tick(0.5);
        Assert.Equal(2, clicker.Currency);

        ticker.TimeScale = 2;
        ticker.Tick(0.5);
        Assert.Equal(3, clicker.Currency);

        ticker.TimeScale = 0.5;
        ticker.Tick(1);
        ticker.Tick(1);
        Assert.Equal(4, clicker.Currency);

        ticker.TimeScale = 0;
        ticker.Tick(1);
        Assert.Equal(4, clicker.Currency);
        Assert.Equal(3, ticker.ElapsedSeconds);
        Assert.Equal(4.5, ticker.UnscaledElapsedSeconds);
    }

    /// <summary>A paused ticker can advance one explicit frame without relying on wall time.</summary>
    [Fact]
    public void StepFrame_AdvancesSimulatedTimeWhileScaleIsZero()
    {
        var (ticker, component) = CreateScene();
        ticker.TimeScale = 0;
        ticker.Tick(1);
        ticker.StepFrame();

        Assert.Equal(SceneTicker.FixedTimestep, ticker.ElapsedSeconds);
        Assert.Equal(1, ticker.UnscaledElapsedSeconds);
        Assert.Equal(1, component.FixedUpdateCount);
    }

    /// <summary>Invalid durations are rejected before entering the fixed-step loop.</summary>
    [Fact]
    public void Tick_RejectsInvalidTime()
    {
        var (ticker, _) = CreateScene();

        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.TimeScale = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.TimeScale = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.Tick(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.Tick(-0.5));
    }

    /// <summary>The scene driver uses its injected clock's interval and retains bounded catch-up backlog.</summary>
    [Fact]
    public void Tick_UsesConfiguredAuthoritativeClock()
    {
        var (_, component) = CreateScene();
        var manager = new SceneManager(assetDatabase);
        var root = component.Node!.Parent!;
        manager.AdoptScene(Guid.NewGuid(), root);
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = 0.02, MaxTicksPerFrame = 2 });
        var ticker = new SceneTicker(manager, clock);

        ticker.Tick(0.1);

        Assert.Same(clock, ticker.Clock);
        Assert.Equal(2, clock.TickIndex);
        Assert.Equal(2, component.FixedUpdateCount);
        Assert.Equal(0.02f, component.LastFixedDeltaTime);
        Assert.Equal(0.06, clock.AccumulatorSeconds, 12);
        clock.IsPaused = true;
        ticker.Tick(1);
        ticker.StepFrame();
        Assert.Equal(3, clock.TickIndex);
        Assert.Equal(3, component.FixedUpdateCount);
        Assert.Equal(0.12, ticker.ElapsedSeconds, 12);
        clock.IsPaused = false;
        ticker.Tick(0);
        Assert.Equal(5, clock.TickIndex);
        ticker.ResetAccumulator();
        Assert.Equal(0, clock.AccumulatorSeconds);
    }

    /// <summary>Presentation durations cannot change the fixed simulation interval or accept invalid time.</summary>
    [Fact]
    public void StepFrame_UsesFixedIntervalAndValidatesPresentationTime()
    {
        var (ticker, component) = CreateScene();
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.StepFrame(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.StepFrame(-1));
        Assert.Throws<ArgumentNullException>(() => new SceneTicker(null!));
        ticker.TimeScale = double.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => ticker.Tick(2));

        ticker.StepFrame(0.5);

        Assert.Equal(1, ticker.Clock.TickIndex);
        Assert.Equal((float)SceneTicker.FixedTimestep, component.LastFixedDeltaTime);
        Assert.Equal(0.5, ticker.ElapsedSeconds);
    }
}
