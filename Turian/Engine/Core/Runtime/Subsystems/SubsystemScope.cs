namespace Turian.Engine.Core;

/// <summary>
/// The subsystems one lifetime owns while it is alive. Resolution walks from this scope to its longer-lived parents
/// and allocates nothing once the scope has started.
/// </summary>
public sealed class SubsystemScope : IDisposable
{
    readonly IReadOnlyList<SubsystemDescriptor> descriptors;
    readonly Dictionary<Type, ISubsystem> instances = [];
    readonly List<ISubsystem> started = [];
    readonly int ownerThread;
    ITickingSubsystem[] fixedTickers = [];
    ITickingSubsystem[] frameTickers = [];
    bool begun;

    internal SubsystemScope(LifetimeScope kind, SubsystemScope? parent, IReadOnlyList<SubsystemDescriptor> descriptors,
        int ownerThread)
    {
        Kind = kind;
        Parent = parent;
        this.descriptors = descriptors;
        this.ownerThread = ownerThread;
    }

    /// <summary>The lifetime this scope stands for.</summary>
    public LifetimeScope Kind { get; }

    /// <summary>The next longer-lived scope, or <c>null</c> for the application.</summary>
    public SubsystemScope? Parent { get; }

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

    /// <summary>Runs every subsystem of <paramref name="phase"/>, in start order.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="deltaTime">Seconds since the previous tick of the phase.</param>
    public void Tick(SubsystemTickPhase phase, float deltaTime)
    {
        EnsureOwnerThread();
        var tickers = phase == SubsystemTickPhase.Fixed ? fixedTickers : frameTickers;
        for (var i = 0; i < tickers.Length; i++)
        {
            tickers[i].Tick(deltaTime);
        }
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
        if (errors is not null)
        {
            throw new AggregateException($"{Kind} subsystems failed to stop.", errors);
        }
    }

    ISubsystem? Find(Type type)
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
