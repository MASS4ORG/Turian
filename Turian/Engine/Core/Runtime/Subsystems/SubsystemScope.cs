namespace Turian.Engine.Core;

/// <summary>
/// The subsystems one lifetime owns while it is alive. Resolution walks from this scope to its longer-lived parents
/// and allocates nothing once the scope has started.
/// </summary>
public sealed class SubsystemScope : IDisposable
{
    readonly IReadOnlyList<SubsystemDescriptor> descriptors;
    readonly IReadOnlyList<SubsystemDescriptor> runOrder;
    readonly Dictionary<Type, ISubsystem> instances = [];
    readonly List<ISubsystem> started = [];
    readonly int ownerThread;
    readonly ISubsystem[][] phases = NoPhases();
    ITickingSubsystem[] fixedTickers = [];
    ITickingSubsystem[] frameTickers = [];
    IFixedTickSystem[] fixedSystems = [];
    bool begun;

    internal SubsystemScope(LifetimeScope kind, SubsystemScope? parent, IReadOnlyList<SubsystemDescriptor> descriptors,
        IReadOnlyList<SubsystemDescriptor> runOrder, int ownerThread, IServiceProvider? services)
    {
        Kind = kind;
        Parent = parent;
        Services = services;
        this.descriptors = descriptors;
        this.runOrder = runOrder;
        this.ownerThread = ownerThread;
    }

    /// <summary>The lifetime this scope stands for.</summary>
    public LifetimeScope Kind { get; }

    /// <summary>The next longer-lived scope, or <c>null</c> for the application.</summary>
    public SubsystemScope? Parent { get; }

    /// <summary>The application's composed services, for factories that need more than other subsystems.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>Resolves a subsystem from this scope or a longer-lived one.</summary>
    /// <typeparam name="T">The type the subsystem was registered as.</typeparam>
    /// <exception cref="InvalidOperationException">No live scope up the chain holds it.</exception>
    public T Get<T>() where T : class, ISubsystem =>
        Find(typeof(T)) as T
        ?? throw new InvalidOperationException($"No live {typeof(T).FullName} subsystem is available to a {Kind} scope.");

    /// <summary>Resolves a subsystem if a live scope up the chain holds it.</summary>
    /// <typeparam name="T">The type the subsystem was registered as.</typeparam>
    /// <param name="subsystem">The subsystem, or <c>null</c>.</param>
    public bool TryGet<T>(out T? subsystem) where T : class, ISubsystem
    {
        subsystem = Find(typeof(T)) as T;
        return subsystem is not null;
    }

    /// <summary>
    /// Runs this lifetime's part of a game-loop phase: every subsystem that runs in <paramref name="phase"/>, in
    /// <see cref="SubsystemRegistry.RunOrder"/>. Allocates nothing.
    /// </summary>
    /// <param name="phase">The phase.</param>
    /// <param name="time">The time the phase runs at.</param>
    public void Run(GameLoopPhase phase, in LoopTime time)
    {
        EnsureOwnerThread();
        var subsystems = phases[(int)phase];
        for (var i = 0; i < subsystems.Length; i++)
        {
            GameLoopPhaseDispatch.Run(subsystems[i], phase, in time);
        }
    }

    /// <summary>Runs every subsystem of <paramref name="phase"/>, in start order.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="deltaTime">Seconds since the previous tick of the phase.</param>
    [Obsolete("Use Run(GameLoopPhase, in LoopTime): FixedTick for SubsystemTickPhase.Fixed, Update for SubsystemTickPhase.Frame. Removed in the next major release.")]
    public void Tick(SubsystemTickPhase phase, float deltaTime)
    {
        EnsureOwnerThread();
        var tickers = phase == SubsystemTickPhase.Fixed ? fixedTickers : frameTickers;
        for (var i = 0; i < tickers.Length; i++)
        {
            tickers[i].Tick(deltaTime);
        }
    }

    /// <summary>Runs the fixed tick of every subsystem that is also an <see cref="IFixedTickSystem"/>, in start order.</summary>
    /// <param name="tick">The tick being executed.</param>
    /// <param name="deltaTime">The fixed interval, in seconds.</param>
    [Obsolete("Use Run(GameLoopPhase.FixedTick, in LoopTime). Removed in the next major release.")]
    public void FixedTick(long tick, double deltaTime)
    {
        EnsureOwnerThread();
        for (var i = 0; i < fixedSystems.Length; i++)
        {
            fixedSystems[i].FixedTick(tick, deltaTime);
        }
    }

    /// <summary>Lets every <see cref="IFixedTickSystem"/> subsystem present the frame after its fixed ticks.</summary>
    /// <param name="alpha">How far presentation time lies between the last tick and the next one, in [0, 1].</param>
    [Obsolete("Use Run(GameLoopPhase.AfterFixedTicks, in LoopTime). Removed in the next major release.")]
    public void AfterFixedTicks(double alpha)
    {
        EnsureOwnerThread();
        for (var i = 0; i < fixedSystems.Length; i++)
        {
            fixedSystems[i].AfterFixedTicks(alpha);
        }
    }

    /// <summary>Resolves a subsystem by its registered type from this scope or a longer-lived one.</summary>
    /// <param name="type">The type the subsystem was registered as.</param>
    /// <returns>The live subsystem, or <c>null</c>.</returns>
    public ISubsystem? Find(Type type)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.instances.TryGetValue(type, out var subsystem))
            {
                return subsystem;
            }
        }

        return null;
    }

    /// <summary>Creates and starts every subsystem of the lifetime, dependencies first; a failure stops those started.</summary>
    /// <exception cref="InvalidOperationException">Called off the thread that created the lifetime.</exception>
    public void Start()
    {
        EnsureOwnerThread();
        if (begun)
        {
            return;
        }

        begun = true;
        try
        {
            foreach (var descriptor in descriptors)
            {
                var subsystem = descriptor.Create(this);
                instances[descriptor.Type] = subsystem;
                try
                {
                    subsystem.Start();
                }
                catch
                {
                    (subsystem as IDisposable)?.Dispose();
                    instances.Remove(descriptor.Type);
                    throw;
                }

                started.Add(subsystem);
            }

            fixedTickers = Tickers(SubsystemTickPhase.Fixed);
            frameTickers = Tickers(SubsystemTickPhase.Frame);
            fixedSystems = [.. started.OfType<IFixedTickSystem>()];
            BuildPhases();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Stops every started subsystem in reverse order, then disposes those that hold resources.</summary>
    /// <exception cref="AggregateException">One or more subsystems failed to stop; the rest were still stopped.</exception>
    public void Dispose()
    {
        EnsureOwnerThread();
        List<Exception>? errors = null;
        for (var i = started.Count - 1; i >= 0; i--)
        {
            var subsystem = started[i];
            try
            {
                subsystem.Stop();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            try
            {
                (subsystem as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        started.Clear();
        instances.Clear();
        fixedTickers = [];
        frameTickers = [];
        fixedSystems = [];
        ClearPhases(phases);
        if (errors is not null)
        {
            throw new AggregateException($"{Kind} subsystems failed to stop.", errors);
        }
    }

    static ISubsystem[][] NoPhases()
    {
        var empty = new ISubsystem[GameLoopPhaseDispatch.Count][];
        ClearPhases(empty);
        return empty;
    }

    static void ClearPhases(ISubsystem[][] tables)
    {
        for (var i = 0; i < tables.Length; i++)
        {
            tables[i] = [];
        }
    }

    // Each phase's table lists the started subsystems that run in it, in the registry's run order.
    void BuildPhases()
    {
        var running = new List<(ISubsystem Subsystem, GameLoopPhases Phases)>(runOrder.Count);
        foreach (var descriptor in runOrder)
        {
            if (instances.TryGetValue(descriptor.Type, out var subsystem))
            {
                running.Add((subsystem, GameLoopPhaseDispatch.PhasesOf(subsystem)));
            }
        }

        for (var phase = 0; phase < phases.Length; phase++)
        {
            var flag = (GameLoopPhases)(1 << phase);
            phases[phase] = [.. running.Where(entry => (entry.Phases & flag) != 0).Select(static entry => entry.Subsystem)];
        }
    }

    ITickingSubsystem[] Tickers(SubsystemTickPhase phase) =>
        [.. started.OfType<ITickingSubsystem>().Where(subsystem => subsystem.TickPhase == phase)];

    void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("Subsystems are started, ticked and stopped on the main thread.");
        }
    }
}
