namespace Turian.Engine.Core;

/// <summary>
/// Drives the per-frame lifecycle of every loaded scene: the fixed-timestep pass
/// (<see cref="Component.OnFixedUpdate"/>), the variable pass (<see cref="Component.OnUpdate"/>)
/// and the late pass (<see cref="Component.OnLateUpdate"/>).
///
/// <para>
/// The ticker is deliberately render-agnostic so that the standalone runtime and the Studio's
/// in-editor play mode share the exact same update semantics.
/// </para>
/// </summary>
/// <remarks>
/// Instances are not thread-safe and are expected to be driven from a single loop thread.
/// </remarks>
/// <param name="sceneManager">The scene manager whose loaded scenes should be ticked.</param>
/// <param name="clock">The session's authoritative clock, or a fresh clock with default settings.</param>
public sealed class SceneTicker(ISceneManager sceneManager, SimulationClock? clock)
{
    /// <summary>Creates a scene driver with a fresh simulation clock using default timing settings.</summary>
    public SceneTicker(ISceneManager sceneManager) : this(sceneManager, null) { }

    /// <summary>The default fixed-update interval, in seconds.</summary>
    public const double FixedTimestep = 1.0 / 60.0;

    static readonly ConditionalWeakTable<Component, HashSet<string>> FailedCallbacks = [];

    readonly ISceneManager sceneManager =
        sceneManager ?? throw new ArgumentNullException(nameof(sceneManager));

    /// <summary>The authoritative fixed-tick clock shared with the running game's services.</summary>
    public SimulationClock Clock { get; } = clock ?? new SimulationClock();

    /// <summary>The optional authoritative world sharing this driver's clock and fixed-tick boundary.</summary>
    public SimulationSession? Simulation { get; init; }

    /// <summary>
    /// Multiplier for simulation time. Zero freezes elapsed game time, but still polls input and runs updates with zero delta.
    /// </summary>
    public double TimeScale
    {
        get => Clock.TimeScale;
        set => Clock.TimeScale = value;
    }

    /// <summary>Seconds advanced by this ticker after applying <see cref="TimeScale"/>.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>Seconds passed to this ticker before applying <see cref="TimeScale"/>.</summary>
    public double UnscaledElapsedSeconds { get; private set; }

    /// <summary>Gets the input source advanced once per tick, or <c>null</c> when input is not routed.</summary>
    public IInputSource? InputSource { get; init; }

    /// <summary>
    /// Gets the action maps recomputed at the start of every tick, or <c>null</c> when no action asset
    /// is in force. Updated before the passes so <see cref="Component.OnUpdate"/> reads this frame.
    /// </summary>
    public InputActionService? Actions { get; init; }

    /// <summary>
    /// Advances every loaded scene by <paramref name="deltaTime"/> unscaled seconds.
    /// Runs fixed steps within the clock's frame budget, retaining backlog, then a single variable and late pass.
    /// </summary>
    /// <param name="deltaTime">Unscaled time elapsed since the previous tick, in seconds.</param>
    public void Tick(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");

        var unscaledDeltaTime = deltaTime;
        var scaledDeltaTime = Clock.IsPaused ? 0d : deltaTime * TimeScale;
        if (!double.IsFinite(scaledDeltaTime))
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Scaled delta time must be finite.");

        UnscaledElapsedSeconds += deltaTime;
        deltaTime = scaledDeltaTime;
        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        var roots = CollectRoots();

        Clock.AdvanceWithCompletion(unscaledDeltaTime,
            (tick, interval) => RunFixedTick(roots, tick, interval), PublishCompletedTick);

        RunUpdate(roots, (float)deltaTime);
        RunLateUpdate(roots, (float)deltaTime);

        InputSource?.NewFrame();
    }

    /// <summary>
    /// Advances every loaded scene by exactly one frame, running a single fixed step
    /// regardless of the accumulator or time scale. Used by the editor's frame-step control.
    /// </summary>
    public void StepFrame() => StepFrame(Clock.FixedDeltaTime);

    /// <summary>Runs one fixed tick and one presentation frame with an explicit presentation delta.</summary>
    /// <param name="deltaTime">The delta time to report to the update callbacks, in seconds.</param>
    public void StepFrame(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");

        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        var roots = CollectRoots();

        Clock.Step((tick, interval) => RunFixedTick(roots, tick, interval));
        PublishCompletedTick();
        RunUpdate(roots, (float)deltaTime);
        RunLateUpdate(roots, (float)deltaTime);

        InputSource?.NewFrame();
    }

    /// <summary>Discards accumulated fixed-step time, e.g. after resuming from a pause.</summary>
    public void ResetAccumulator() => Clock.ResetAccumulator();

    List<Node> CollectRoots() =>
        [.. sceneManager.LoadedScenes
            .Select(scene => scene.RootNode)
            .Prepend(sceneManager.PersistentRoot)];

    void RunFixedTick(List<Node> roots, long tick, double interval)
    {
        var simulation = Simulation;
        if (simulation is not null && !ReferenceEquals(simulation.Clock, Clock))
            throw new InvalidOperationException("The scene and simulation must share one clock.");
        RunFixedUpdate(roots, (float)interval);
        simulation?.RunTick(tick, interval);
    }

    void PublishCompletedTick() => Simulation?.PublishCompletedTick();

    static void RunFixedUpdate(List<Node> roots, float fixedDeltaTime)
    {
        foreach (var node in ActiveNodes(roots))
        {
            node.OnFixedUpdate(fixedDeltaTime);
            foreach (var component in node.Components)
            {
                if (!component.IsActive) continue;
                try
                {
                    component.OnFixedUpdate(fixedDeltaTime);
                }
                catch (Exception ex)
                {
                    LogComponentException(component, nameof(Component.OnFixedUpdate), ex);
                }
            }
        }
    }

    static void RunUpdate(List<Node> roots, float deltaTime)
    {
        foreach (var node in ActiveNodes(roots))
        {
            node.OnUpdate(deltaTime);
            foreach (var component in node.Components)
            {
                if (!component.IsActive) continue;
                try
                {
                    component.EnsureStarted();
                    component.OnUpdate(deltaTime);
                }
                catch (Exception ex)
                {
                    LogComponentException(component, nameof(Component.OnUpdate), ex);
                }
            }
        }
    }

    static void RunLateUpdate(List<Node> roots, float deltaTime)
    {
        foreach (var node in ActiveNodes(roots))
        {
            node.OnLateUpdate(deltaTime);
            foreach (var component in node.Components)
            {
                if (!component.IsActive) continue;
                try
                {
                    component.OnLateUpdate(deltaTime);
                }
                catch (Exception ex)
                {
                    LogComponentException(component, nameof(Component.OnLateUpdate), ex);
                }
            }
        }
    }

    /// <summary>
    /// Logs a script's exception and lets the frame go on. One faulty component
    /// must not stop every other script or tear down the game loop. Each component and callback is
    /// reported once, since a callback that throws usually throws on every frame.
    /// </summary>
    static void LogComponentException(Component component, string callback, Exception ex)
    {
        if (!FailedCallbacks.GetOrCreateValue(component).Add(callback)) return;

        Log.Logger.LogError(ex, "{Component} on {Node} threw in {Callback}",
            component.GetType().Name, component.Node?.Name, callback);
    }

    /// <summary>
    /// Materialises the active nodes of every root up-front so that lifecycle callbacks
    /// may add or remove nodes without invalidating the iteration.
    /// </summary>
    static List<Node> ActiveNodes(List<Node> roots)
    {
        var nodes = new List<Node>();
        foreach (var root in roots)
        {
            if (!root.IsActive) continue;
            nodes.Add(root);
            nodes.AddRange(Node.GetChildren(root));
        }
        return nodes;
    }
}
