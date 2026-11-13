namespace Turian.Engine.Core;

/// <summary>Game code's handle on the gameplay session: its subsystems, each loaded scene's subsystems, and a restart.</summary>
public interface ISessionLifetime
{
    /// <summary>The running session's scope, or <c>null</c> between sessions.</summary>
    SubsystemScope? Session { get; }

    /// <summary>The scope of the loaded scene that holds <paramref name="node"/>.</summary>
    /// <param name="node">A node in a loaded scene.</param>
    /// <returns>The scene's scope, or <c>null</c> for a node outside every loaded scene.</returns>
    SubsystemScope? SceneOf(Node node);

    /// <summary>
    /// Ends the session at the end of the current frame and begins a fresh one: every scene unloads, the session's
    /// subsystems are recreated, then <paramref name="sceneAssetId"/> loads, or the game's first scene when null.
    /// </summary>
    /// <param name="sceneAssetId">The scene to load into the new session, or <c>null</c> for the game's first scene.</param>
    void Restart(Guid? sceneAssetId = null);
}

/// <summary>
/// Binds a <see cref="LifetimeHost"/> to a running game: a scene scope begins with each loaded scene and ends when it
/// unloads, and a requested session restart is applied between frames.
/// </summary>
public sealed class GameLifetimes : ISessionLifetime, IDisposable
{
    readonly SceneManager scenes;
    readonly Func<Guid?, Task> reload;
    readonly Dictionary<LoadedScene, SubsystemScope> sceneScopes = new(ReferenceEqualityComparer.Instance);
    bool restartPending;
    Guid? restartScene;
    bool disposed;

    /// <summary>Binds <paramref name="host"/> to the scenes <paramref name="scenes"/> loads.</summary>
    /// <param name="host">The lifetimes, created on the game-loop thread.</param>
    /// <param name="scenes">The game's scene manager.</param>
    /// <param name="reload">Loads the scene that opens a restarted session; it receives the requested scene, if any.</param>
    public GameLifetimes(LifetimeHost host, SceneManager scenes, Func<Guid?, Task> reload)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(reload);
        Host = host;
        this.scenes = scenes;
        this.reload = reload;
        scenes.SceneLoaded += OnSceneLoaded;
        scenes.SceneUnloaded += OnSceneUnloaded;
    }

    /// <summary>The lifetimes this binding drives.</summary>
    public LifetimeHost Host { get; }

    /// <inheritdoc />
    public SubsystemScope? Session => Host.Session;

    /// <summary>The running session's DataAssets, or <c>null</c> between sessions.</summary>
    public DataAssetSession? DataAssets => Session?.Find(typeof(DataAssetSession)) as DataAssetSession;

    /// <summary>Starts the application's subsystems and begins the first session.</summary>
    public void Start()
    {
        Host.Start();
        Host.BeginSession();
    }

    /// <inheritdoc />
    public SubsystemScope? SceneOf(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var root = node;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        foreach (var (scene, scope) in sceneScopes)
        {
            if (ReferenceEquals(scene.RootNode, root))
            {
                return scope;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Restart(Guid? sceneAssetId = null)
    {
        restartPending = true;
        restartScene = sceneAssetId;
    }

    /// <summary>Applies a requested restart; the game loop calls it once each frame has finished.</summary>
    public void ApplyPendingRestart()
    {
        if (!restartPending)
        {
            return;
        }

        restartPending = false;
        scenes.UnloadAllScenes();
        Host.EndSession();
        Host.BeginSession();
        reload(restartScene).ContinueWith(
            static load => Log.Logger.LogError(load.Exception, "The restarted session's first scene failed to load"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Ends every scene, the session and the application, shortest-lived first.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        scenes.SceneLoaded -= OnSceneLoaded;
        scenes.SceneUnloaded -= OnSceneUnloaded;
        sceneScopes.Clear();
        Host.Dispose();
    }

    void OnSceneLoaded(LoadedScene scene) => sceneScopes[scene] = Host.BeginScene();

    void OnSceneUnloaded(LoadedScene scene)
    {
        if (sceneScopes.Remove(scene, out var scope))
        {
            Host.EndScene(scope);
        }
    }
}

/// <summary>
/// Resolves live subsystems ahead of the composed services, so a scene object injected with a subsystem type receives
/// the running session's instance rather than one fixed when the services were built.
/// </summary>
/// <param name="services">The composed services.</param>
/// <param name="host">The lifetimes whose session, else application, subsystems resolve first.</param>
public sealed class SubsystemServiceProvider(IServiceProvider services, LifetimeHost host) : IServiceProvider
{
    /// <inheritdoc />
    public object? GetService(Type serviceType) =>
        typeof(ISubsystem).IsAssignableFrom(serviceType) && host.Current.Find(serviceType) is { } subsystem
            ? subsystem
            : services.GetService(serviceType);
}
