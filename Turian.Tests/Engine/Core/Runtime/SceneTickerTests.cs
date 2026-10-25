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
            if (Input?.WasKeyPressed(Key.Space) == true)
            {
                Currency++;
            }

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

    /// <summary>Lifecycle passes reuse their traversal buffers, so a frame's allocations do not grow with the node count.</summary>
    [Fact]
    public void Tick_AllocationsDoNotGrowWithNodeCount()
    {
        var small = MeasureTickAllocations(10);
        var large = MeasureTickAllocations(5000);

        Assert.True(large - small < 1024, $"10 nodes allocated {small} B per frame, 5000 nodes {large} B.");
    }

    long MeasureTickAllocations(int nodeCount)
    {
        var root = new Node();
        for (var i = 0; i < nodeCount; i++)
        {
            var child = new Node();
            child.Components.Add(new CountingComponent());
            root.Children.Add(child);
        }

        root.Awake(null);
        var manager = new SceneManager(assetDatabase);
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);
        for (var i = 0; i < 3; i++)
        {
            ticker.StepFrame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        ticker.StepFrame();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>A component that removes itself and adds another mid-pass neither aborts the frame nor skips its sibling.</summary>
    [Fact]
    public void Tick_ToleratesComponentsChangingTheirNodeDuringAPass()
    {
        var root = new Node();
        var replacing = new ReplacingComponent();
        var sibling = new CountingComponent();
        root.Components.Add(replacing);
        root.Components.Add(sibling);
        root.Awake(null);
        var manager = new SceneManager(assetDatabase);
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);

        ticker.StepFrame();

        Assert.DoesNotContain(replacing, root.Components);
        Assert.Equal(1, sibling.UpdateCount);
        Assert.Equal(1, sibling.LateUpdateCount);
        Assert.Single(root.GetComponents<CountingComponent>(), c => !ReferenceEquals(c, sibling));
    }

    /// <summary>
    /// Despawning nodes from an update, including the updating node and siblings before and after it, does not abort
    /// the frame. Nodes already listed for the running pass finish it, then get no later pass or frame.
    /// </summary>
    [Fact]
    public void Tick_ToleratesDespawningNodesDuringUpdate()
    {
        var root = new Node();
        Node early = new(), despawner = new(), late = new(), survivor = new();
        var counters = new[] { early, despawner, late, survivor }.Select(static node =>
        {
            var counter = new CountingComponent();
            node.Components.Add(counter);
            return counter;
        }).ToArray();
        despawner.Components.Insert(0, new DespawningComponent { Targets = [early, despawner, late] });
        root.Children = [early, despawner, late, survivor];
        root.Awake(null);
        var manager = new SceneManager(assetDatabase);
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);

        ticker.StepFrame();
        ticker.StepFrame();

        Assert.Equal([survivor], root.Children);
        Assert.All(new[] { early, despawner, late }, static node => Assert.Null(node.Parent));
        Assert.All(counters[..3], static counter =>
        {
            Assert.Equal(1, counter.FixedUpdateCount);
            Assert.Equal(1, counter.UpdateCount);
            Assert.Equal(0, counter.LateUpdateCount);
        });
        Assert.Equal(2, counters[3].UpdateCount);
        Assert.Equal(2, counters[3].LateUpdateCount);
    }

    sealed class DespawningComponent : Component
    {
        public List<Node> Targets { get; init; } = [];

        public override void OnUpdate(float deltaTime)
        {
            foreach (var target in Targets)
            {
                target.Parent?.Children.Remove(target);
            }

            Targets.Clear();
        }
    }

    sealed class ReplacingComponent : Component
    {
        public override void OnUpdate(float deltaTime)
        {
            var node = Node!;
            node.RemoveComponent(this);
            node.AddComponent(new CountingComponent());
        }
    }

    /// <summary>
    /// Only overridden frame callbacks are dispatched: an inherited override still runs, and a component without one
    /// still starts once.
    /// </summary>
    [Fact]
    public void Tick_DispatchesOverriddenCallbacksOnly()
    {
        var root = new Node();
        var derived = new DerivedUpdatingComponent();
        var starting = new StartingComponent();
        root.Components.Add(derived);
        root.Components.Add(starting);
        root.Awake(null);
        var manager = new SceneManager(assetDatabase);
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager);

        ticker.StepFrame();
        ticker.StepFrame();

        Assert.Equal(2, derived.UpdateCount);
        Assert.Equal(1, starting.StartCount);
        Assert.True(starting.IsStarted);
        Assert.True(derived.Overrides(Component.FrameCallbacks.Update));
        Assert.False(derived.Overrides(Component.FrameCallbacks.LateUpdate | Component.FrameCallbacks.FixedUpdate));
        Assert.False(starting.Overrides(Component.FrameCallbacks.Update | Component.FrameCallbacks.LateUpdate |
                                        Component.FrameCallbacks.FixedUpdate));
        Assert.True(new CountingComponent().Overrides(Component.FrameCallbacks.FixedUpdate));
    }

    class UpdatingComponent : Component
    {
        public int UpdateCount { get; private set; }

        public override void OnUpdate(float deltaTime) => UpdateCount++;
    }

    sealed class DerivedUpdatingComponent : UpdatingComponent;

    sealed class StartingComponent : Component
    {
        public int StartCount { get; private set; }

        public override void OnStart() => StartCount++;
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
