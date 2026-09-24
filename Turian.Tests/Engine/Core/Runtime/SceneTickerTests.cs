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
        TestAssetDatabase.Reset();
        assetDatabase = new AssetDatabase();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        GC.SuppressFinalize(this);
    }

    [TypeId("a4000001-0000-4000-8000-000000000001")]
    sealed class CountingComponent : Component
    {
        public int StartCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int LateUpdateCount { get; private set; }
        public int FixedUpdateCount { get; private set; }
        public int UpdateCountAtFirstStart { get; private set; } = -1;

        public override void OnStart()
        {
            StartCount++;
            UpdateCountAtFirstStart = UpdateCount;
        }

        public override void OnUpdate(float deltaTime) => UpdateCount++;
        public override void OnLateUpdate(float deltaTime) => LateUpdateCount++;
        public override void OnFixedUpdate(float fixedDeltaTime) => FixedUpdateCount++;
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

        input.PushKeyDown(Silk.NET.Input.Key.W);
        Assert.True(input.WasKeyPressed(Silk.NET.Input.Key.W));

        ticker.Tick(0.016);

        Assert.False(input.WasKeyPressed(Silk.NET.Input.Key.W));
        Assert.True(input.IsKeyDown(Silk.NET.Input.Key.W));
    }
}
