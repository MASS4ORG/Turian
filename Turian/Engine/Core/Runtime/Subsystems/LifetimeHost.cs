namespace Turian.Engine.Core;

/// <summary>
/// Owns the three lifetimes of a running game: the application scope that lives as long as the host, at most one
/// gameplay session that survives scene loads, and a scope per loaded scene. Create it on the main thread, which
/// every start, tick and stop must then use.
/// </summary>
public sealed class LifetimeHost : IDisposable
{
    readonly SubsystemRegistry registry;
    readonly IServiceProvider? services;
    readonly List<SubsystemScope> scenes = [];
    readonly int ownerThread = Environment.CurrentManagedThreadId;

    /// <summary>Creates a host whose scopes build their subsystems from <paramref name="registry"/>.</summary>
    /// <param name="registry">The validated subsystems.</param>
    /// <param name="services">The application's composed services, handed to subsystem factories.</param>
    public LifetimeHost(SubsystemRegistry registry, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
        this.services = services;
        Application = NewScope(LifetimeScope.Application, null);
    }

    /// <summary>The scope that lives as long as the host.</summary>
    public SubsystemScope Application { get; }

    /// <summary>The running gameplay session's scope, or <c>null</c> between sessions.</summary>
    public SubsystemScope? Session { get; private set; }

    /// <summary>The longest chain of live scopes outside any scene: the session when one runs, else the application.</summary>
    public SubsystemScope Current => Session ?? Application;

    /// <summary>Starts the application's subsystems.</summary>
    public void Start() => Application.Start();

    /// <summary>Starts a gameplay session whose subsystems survive scene loads until <see cref="EndSession"/>.</summary>
    /// <exception cref="InvalidOperationException">A session is already running.</exception>
    public SubsystemScope BeginSession()
    {
        if (Session is not null)
        {
            throw new InvalidOperationException("A session is already running; end it before beginning another.");
        }

        var session = NewScope(LifetimeScope.Session, Application);
        session.Start();
        Session = session;
        return session;
    }

    /// <summary>Ends the running session, unloading its scenes' scopes first; without a session it does nothing.</summary>
    public void EndSession()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            EndScenes();
        }
        finally
        {
            Session = null;
            session.Dispose();
        }
    }

    /// <summary>Starts the subsystems of a loaded scene, resolving the session's and the application's too.</summary>
    /// <returns>The scene's scope; pass it to <see cref="EndScene"/> when the scene unloads.</returns>
    public SubsystemScope BeginScene()
    {
        var scene = NewScope(LifetimeScope.Scene, Session ?? Application);
        scene.Start();
        scenes.Add(scene);
        return scene;
    }

    /// <summary>Ends a scene's subsystems when the scene unloads.</summary>
    /// <param name="scene">The scope <see cref="BeginScene"/> returned.</param>
    public void EndScene(SubsystemScope scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scenes.Remove(scene))
        {
            scene.Dispose();
        }
    }

    /// <summary>Runs the ticking subsystems of <paramref name="phase"/>: application, then session, then scenes.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="deltaTime">Seconds since the previous tick of the phase.</param>
    public void Tick(SubsystemTickPhase phase, float deltaTime)
    {
        Application.Tick(phase, deltaTime);
        Session?.Tick(phase, deltaTime);
        for (var i = 0; i < scenes.Count; i++)
        {
            scenes[i].Tick(phase, deltaTime);
        }
    }

    /// <summary>Runs the <see cref="IFixedTickSystem"/> subsystems: application, then session, then scenes.</summary>
    /// <param name="tick">The tick being executed.</param>
    /// <param name="deltaTime">The fixed interval, in seconds.</param>
    public void FixedTick(long tick, double deltaTime)
    {
        Application.FixedTick(tick, deltaTime);
        Session?.FixedTick(tick, deltaTime);
        for (var i = 0; i < scenes.Count; i++)
        {
            scenes[i].FixedTick(tick, deltaTime);
        }
    }

    /// <summary>Lets the <see cref="IFixedTickSystem"/> subsystems present the frame, in the order they tick.</summary>
    /// <param name="alpha">How far presentation time lies between the last tick and the next one, in [0, 1].</param>
    public void AfterFixedTicks(double alpha)
    {
        Application.AfterFixedTicks(alpha);
        Session?.AfterFixedTicks(alpha);
        for (var i = 0; i < scenes.Count; i++)
        {
            scenes[i].AfterFixedTicks(alpha);
        }
    }

    /// <summary>Ends every scene, the session and the application, shortest-lived first.</summary>
    public void Dispose()
    {
        try
        {
            EndSession();
        }
        finally
        {
            try
            {
                EndScenes();
            }
            finally
            {
                Application.Dispose();
            }
        }
    }

    SubsystemScope NewScope(LifetimeScope kind, SubsystemScope? parent) =>
        new(kind, parent, registry.For(kind), ownerThread, services);

    void EndScenes()
    {
        List<Exception>? errors = null;
        for (var i = scenes.Count - 1; i >= 0; i--)
        {
            var scene = scenes[i];
            scenes.RemoveAt(i);
            try
            {
                scene.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("Scene subsystems failed to stop.", errors);
        }
    }
}
