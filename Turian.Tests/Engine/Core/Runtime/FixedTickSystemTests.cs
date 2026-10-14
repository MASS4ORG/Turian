namespace Turian.Tests;

/// <summary>Session systems driven on the fixed-tick boundary, transform versions and brick module discovery.</summary>
public class FixedTickSystemTests
{
    sealed class RecordingComponent(List<string> log) : Component
    {
        public override void OnFixedUpdate(float fixedDeltaTime) => log.Add("component-fixed");

        public override void OnUpdate(float deltaTime) => log.Add("component-update");
    }

    sealed class RecordingSystem(List<string> log) : IFixedTickSystem
    {
        public List<double> Alphas { get; } = [];

        public void FixedTick(long tick, double deltaTime) => log.Add($"system-fixed-{tick}");

        public void AfterFixedTicks(double alpha)
        {
            Alphas.Add(alpha);
            log.Add("system-after");
        }
    }

    static (SceneTicker Ticker, RecordingSystem System, List<string> Log) CreateTicker()
    {
        var log = new List<string>();
        var sceneManager = new SceneManager(new AssetDatabase());
        var root = new Node();
        root.AddComponent(new RecordingComponent(log));
        root.Awake(null);
        sceneManager.AdoptScene(Guid.NewGuid(), root);
        var system = new RecordingSystem(log);
        var clock = new SimulationClock(new TimeSettings { FixedDeltaTime = 0.1 });
        return (new SceneTicker(sceneManager, clock) { Systems = [system] }, system, log);
    }

    /// <summary>Systems run after the components of each fixed tick, then once before the variable pass.</summary>
    [Fact]
    public void Tick_RunsSystemsAfterComponentsAndBeforeUpdate()
    {
        var (ticker, _, log) = CreateTicker();

        ticker.Tick(0.25);

        Assert.Equal(
        [
            "component-fixed", "system-fixed-1", "component-fixed", "system-fixed-2", "system-after",
            "component-update",
        ], log);
    }

    /// <summary>The interpolation factor is the unconsumed fraction of the next fixed tick.</summary>
    [Fact]
    public void Tick_PassesAccumulatorFractionAsAlpha()
    {
        var (ticker, system, _) = CreateTicker();

        ticker.Tick(0.25);
        ticker.Tick(0.02);

        Assert.Equal(0.5, system.Alphas[0], 6);
        Assert.Equal(0.7, system.Alphas[1], 6);
    }

    /// <summary>A single-step frame also notifies systems before the variable pass.</summary>
    [Fact]
    public void StepFrame_RunsSystemsOnce()
    {
        var (ticker, system, log) = CreateTicker();

        ticker.StepFrame();

        Assert.Equal(["component-fixed", "system-fixed-1", "system-after", "component-update"], log);
        Assert.Single(system.Alphas);
    }

    /// <summary>Moving an ancestor changes a descendant's version even when the descendant's cache is already stale.</summary>
    [Fact]
    public void TransformVersion_ChangesWithAncestors()
    {
        var root = new Node();
        var child = new Node();
        var grandchild = new Node();
        child.Children.Add(grandchild);
        root.Children.Add(child);
        root.Awake(null);
        var before = grandchild.TransformVersion;

        root.Position = new Vector3(1, 0, 0);
        var afterRoot = grandchild.TransformVersion;
        root.Position = new Vector3(2, 0, 0);

        Assert.NotEqual(before, afterRoot);
        Assert.NotEqual(afterRoot, grandchild.TransformVersion);
        var stable = grandchild.TransformVersion;
        Assert.Equal(stable, grandchild.TransformVersion);
    }

    /// <summary>
    /// Brick assemblies a host registers join the module graph, even when the game assembly never references them.
    /// </summary>
    [Fact]
    public void Graph_IncludesRegisteredBrickAssemblies()
    {
        var brick = typeof(global::Turian.Engine.UI.UiModule).Assembly;
        var game = typeof(global::Turian.Engine.Save.SaveService).Assembly;

        PrecastAssemblies.Register(brick);
        PrecastAssemblies.Register(brick);

        Assert.Single(PrecastAssemblies.Loaded, assembly => assembly == brick);
        var graph = EngineServiceModules.Graph(game);
        Assert.Contains(brick, graph);
        Assert.Same(game, graph[^1]);
    }
}
