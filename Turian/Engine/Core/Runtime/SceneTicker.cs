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
public sealed class SceneTicker(ISceneManager sceneManager)
{
    /// <summary>The fixed-update interval, in seconds.</summary>
    public const double FixedTimestep = 1.0 / 60.0;

    static readonly ConditionalWeakTable<Component, HashSet<string>> FailedCallbacks = [];

    readonly ISceneManager sceneManager =
        sceneManager ?? throw new ArgumentNullException(nameof(sceneManager));

    double fixedAccumulator;
    double timeScale = 1d;

    /// <summary>
    /// Multiplier for simulation time. Zero freezes elapsed game time, but still polls input and runs updates with zero delta.
    /// </summary>
    public double TimeScale
    {
        get => timeScale;
        set
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Time scale must be finite and non-negative.");
            timeScale = value;
        }
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
    /// Runs as many fixed steps as the accumulated time allows, then a single variable
    /// and late pass.
    /// </summary>
    /// <param name="deltaTime">Unscaled time elapsed since the previous tick, in seconds.</param>
    public void Tick(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");

        var scaledDeltaTime = deltaTime * TimeScale;
        if (!double.IsFinite(scaledDeltaTime))
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Scaled delta time must be finite.");

        UnscaledElapsedSeconds += deltaTime;
        deltaTime = scaledDeltaTime;
        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        var roots = CollectRoots();

        fixedAccumulator += deltaTime;
        while (fixedAccumulator >= FixedTimestep)
        {
            RunFixedUpdate(roots);
            fixedAccumulator -= FixedTimestep;
        }

        RunUpdate(roots, (float)deltaTime);
        RunLateUpdate(roots, (float)deltaTime);

        InputSource?.NewFrame();
    }

    /// <summary>
    /// Advances every loaded scene by exactly one frame, running a single fixed step
    /// regardless of the accumulator or time scale. Used by the editor's frame-step control.
    /// </summary>
    /// <param name="deltaTime">The delta time to report to the update callbacks, in seconds.</param>
    public void StepFrame(double deltaTime = FixedTimestep)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");

        ElapsedSeconds += deltaTime;
        sceneManager.EnsureScenesStarted();
        Actions?.Update();

        var roots = CollectRoots();

        RunFixedUpdate(roots);
        RunUpdate(roots, (float)deltaTime);
        RunLateUpdate(roots, (float)deltaTime);

        InputSource?.NewFrame();
    }

    /// <summary>Discards accumulated fixed-step time, e.g. after resuming from a pause.</summary>
    public void ResetAccumulator() => fixedAccumulator = 0d;

    List<Node> CollectRoots() =>
        [.. sceneManager.LoadedScenes
            .Select(scene => scene.RootNode)
            .Prepend(sceneManager.PersistentRoot)];

    static void RunFixedUpdate(List<Node> roots)
    {
        foreach (var node in ActiveNodes(roots))
        {
            node.OnFixedUpdate((float)FixedTimestep);
            foreach (var component in node.Components)
            {
                if (!component.IsActive) continue;
                try
                {
                    component.OnFixedUpdate((float)FixedTimestep);
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
