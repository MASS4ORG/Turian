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

    // Per-frame traversal state is reused so a frame does not allocate in proportion to the node count.
    readonly List<Node> roots = [];
    readonly Stack<Node> pending = new();
    List<Node>? nodeBuffer = [];
    Action<long, double>? fixedTick;
    Action? completedTick;

    /// <summary>The authoritative fixed-tick clock shared with the running game's services.</summary>
    public SimulationClock Clock { get; } = clock ?? new SimulationClock();

    /// <summary>The gameplay scheduler owned by this running session.</summary>
    public GameLoopScheduler Async { get; init; } = new();

    /// <summary>The optional authoritative world sharing this driver's clock and fixed-tick boundary.</summary>
    public SimulationSession? Simulation { get; init; }

    /// <summary>Session systems run after the components of every fixed tick, in list order.</summary>
    public IReadOnlyList<IFixedTickSystem> Systems { get; init; } = [];

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
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");
        }

        var unscaledDeltaTime = deltaTime;
        var scaledDeltaTime = Clock.IsPaused ? 0d : deltaTime * TimeScale;
        if (!double.IsFinite(scaledDeltaTime))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Scaled delta time must be finite.");
        }

        using var asyncContext = Async.EnterContext();
        Async.BeginFrame(scaledDeltaTime, unscaledDeltaTime);
        UnscaledElapsedSeconds += deltaTime;
        deltaTime = scaledDeltaTime;
        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        CollectRoots();

        Clock.AdvanceWithCompletion(unscaledDeltaTime, fixedTick ??= RunFixedTick,
            completedTick ??= PublishCompletedTick);

        RunAfterFixedTicks();
        RunUpdate((float)deltaTime);
        Async.DrainUpdate();
        RunLateUpdate((float)deltaTime);
        Async.DrainLateUpdate();

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
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");
        }

        using var asyncContext = Async.EnterContext();
        Async.BeginFrame(deltaTime, deltaTime);
        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        CollectRoots();

        Clock.Step(fixedTick ??= RunFixedTick);
        PublishCompletedTick();
        RunAfterFixedTicks();
        RunUpdate((float)deltaTime);
        Async.DrainUpdate();
        RunLateUpdate((float)deltaTime);
        Async.DrainLateUpdate();

        InputSource?.NewFrame();
    }

    /// <summary>Discards accumulated fixed-step time, e.g. after resuming from a pause.</summary>
    public void ResetAccumulator() => Clock.ResetAccumulator();

    void CollectRoots()
    {
        roots.Clear();
        roots.Add(sceneManager.PersistentRoot);
        foreach (var scene in sceneManager.LoadedScenes)
        {
            roots.Add(scene.RootNode);
        }
    }

    void RunFixedTick(long tick, double interval)
    {
        var simulation = Simulation;
        if (simulation is not null && !ReferenceEquals(simulation.Clock, Clock))
        {
            throw new InvalidOperationException("The scene and simulation must share one clock.");
        }

        RunFixedUpdate((float)interval);
        foreach (var system in Systems)
        {
            system.FixedTick(tick, interval);
        }

        simulation?.RunTick(tick, interval);
    }

    void RunAfterFixedTicks()
    {
        if (Systems.Count == 0)
        {
            return;
        }

        var alpha = Math.Clamp(Clock.AccumulatorSeconds / Clock.FixedDeltaTime, 0d, 1d);
        foreach (var system in Systems)
        {
            system.AfterFixedTicks(alpha);
        }
    }

    void PublishCompletedTick()
    {
        Simulation?.PublishCompletedTick();
        Async.CompleteFixedStep(Clock.FixedDeltaTime);
    }

    void RunFixedUpdate(float fixedDeltaTime)
    {
        var nodes = RentActiveNodes();
        foreach (var node in nodes)
        {
            node.OnFixedUpdate(fixedDeltaTime);
            var components = node.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.IsActive)
                {
                    try
                    {
                        component.OnFixedUpdate(fixedDeltaTime);
                    }
                    catch (Exception ex)
                    {
                        LogComponentException(component, nameof(Component.OnFixedUpdate), ex);
                    }
                }

                i = IndexAfterCallback(components, component, i);
            }
        }

        ReturnActiveNodes(nodes);
    }

    void RunUpdate(float deltaTime)
    {
        var nodes = RentActiveNodes();
        foreach (var node in nodes)
        {
            node.OnUpdate(deltaTime);
            var components = node.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.IsActive)
                {
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

                i = IndexAfterCallback(components, component, i);
            }
        }

        ReturnActiveNodes(nodes);
    }

    void RunLateUpdate(float deltaTime)
    {
        var nodes = RentActiveNodes();
        foreach (var node in nodes)
        {
            node.OnLateUpdate(deltaTime);
            var components = node.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.IsActive)
                {
                    try
                    {
                        component.OnLateUpdate(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        LogComponentException(component, nameof(Component.OnLateUpdate), ex);
                    }
                }

                i = IndexAfterCallback(components, component, i);
            }
        }

        ReturnActiveNodes(nodes);
    }

    /// <summary>
    /// Returns the index the pass continues after, because a callback may add components or remove its own or an
    /// earlier one; a removed current component resumes at the slot it vacated.
    /// </summary>
    static int IndexAfterCallback(Collection<Component> components, Component current, int index)
    {
        if (index < components.Count && ReferenceEquals(components[index], current))
        {
            return index;
        }

        for (var i = 0; i < components.Count; i++)
        {
            if (ReferenceEquals(components[i], current))
            {
                return i;
            }
        }

        return index - 1;
    }

    /// <summary>
    /// Logs a script's exception and lets the frame go on. One faulty component
    /// must not stop every other script or tear down the game loop. Each component and callback is
    /// reported once, since a callback that throws usually throws on every frame.
    /// </summary>
    static void LogComponentException(Component component, string callback, Exception ex)
    {
        if (!FailedCallbacks.GetOrCreateValue(component).Add(callback))
        {
            return;
        }

        Log.Logger.LogError(ex, "{Component} on {Node} threw in {Callback}",
            component.GetType().Name, component.Node?.Name, callback);
    }

    /// <summary>
    /// Materialises the active nodes of every root up-front, depth-first in hierarchy order, so that lifecycle
    /// callbacks may add or remove nodes without invalidating the iteration. The reused buffer is taken while a pass
    /// runs, so a re-entrant tick gets its own list.
    /// </summary>
    List<Node> RentActiveNodes()
    {
        var nodes = nodeBuffer ?? [];
        nodeBuffer = null;
        foreach (var root in roots)
        {
            if (!root.IsActive)
            {
                continue;
            }

            nodes.Add(root);
            Node.PushActiveChildren(root, pending);
            while (pending.TryPop(out var current))
            {
                nodes.Add(current);
                Node.PushActiveChildren(current, pending);
            }
        }

        return nodes;
    }

    void ReturnActiveNodes(List<Node> nodes)
    {
        nodes.Clear();
        nodeBuffer = nodes;
    }
}
