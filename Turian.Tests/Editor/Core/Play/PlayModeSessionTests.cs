namespace Turian.Tests;

/// <summary>Play Mode runs one gameplay session from Play to Stop, restartable in place without touching the edited scene.</summary>
[Collection(SerialTests.Name)]
public sealed class PlayModeSessionTests : IDisposable
{
    /// <summary>Receives the session's subsystems and the session handle as a game script would.</summary>
    [TypeId("a4000003-0000-4000-8000-000000000101")]
    sealed class SessionProbe : Component
    {
        [InjectService, JsonIgnore]
        public DataAssetSession? DataAssets { get; private set; }

        [InjectService, JsonIgnore]
        public ISessionLifetime? Lifetime { get; private set; }
    }

    sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    readonly SceneTreeController sceneTree;
    readonly PlayModeService playMode;
    readonly Node editorRoot = new() { Name = "Edited" };

    /// <summary>Opens a scene holding one probe.</summary>
    public PlayModeSessionTests()
    {
        var database = new AssetDatabase();
        sceneTree = new SceneTreeController(new AssetManager(), new SettingsService(), assetImporter: null!,
            assetLoader: new RuntimeAssetLoader(database), sceneManager: Substitute.For<ISceneManager>(),
            database: database);
        editorRoot.AddComponent(new SessionProbe());
        editorRoot.Awake(null);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, editorRoot);
        playMode = new PlayModeService(sceneTree, database, new NoServices(), Log.Logger);
    }

    /// <inheritdoc />
    public void Dispose() => playMode.Stop();

    /// <summary>Scripts receive the running session's DataAssets and the session handle.</summary>
    [Fact]
    public void ScriptsReceiveTheSessionsSubsystems()
    {
        Assert.True(playMode.Start());

        var probe = playMode.PlayRoot!.GetComponent<SessionProbe>()!;
        Assert.NotNull(playMode.DataAssets);
        Assert.Same(playMode.DataAssets, probe.DataAssets);
        Assert.Same(playMode.Lifetimes, probe.Lifetime);
        Assert.Null(editorRoot.GetComponent<SessionProbe>()!.DataAssets);
    }

    /// <summary>A restart replaces the session and the running copy; Stop discards the session.</summary>
    [Fact]
    public void RestartReplacesTheSessionAndStopDiscardsIt()
    {
        Assert.True(playMode.Start());
        var firstSession = playMode.DataAssets;
        var firstCopy = playMode.PlayRoot;

        playMode.Lifetimes!.Restart();
        playMode.Tick(1 / 60d);

        Assert.NotNull(playMode.DataAssets);
        Assert.NotSame(firstSession, playMode.DataAssets);
        Assert.NotSame(firstCopy, playMode.PlayRoot);
        Assert.NotSame(editorRoot, playMode.PlayRoot);
        Assert.Same(playMode.DataAssets, playMode.PlayRoot!.GetComponent<SessionProbe>()!.DataAssets);

        playMode.Stop();

        Assert.Null(playMode.Lifetimes);
        Assert.Null(playMode.DataAssets);
        Assert.Null(playMode.Services);
    }

    /// <summary>A restart onto a scene missing from the catalog logs the failure and keeps the new session playing.</summary>
    [Fact]
    public void RestartOntoAMissingSceneKeepsPlaying()
    {
        Assert.True(playMode.Start());
        var firstSession = playMode.DataAssets;

        playMode.Lifetimes!.Restart(Guid.NewGuid());
        playMode.Tick(1 / 60d);

        Assert.True(playMode.IsActive);
        Assert.NotNull(playMode.DataAssets);
        Assert.NotSame(firstSession, playMode.DataAssets);
    }

    sealed class FailingObserverLookup : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDataAssetEventObserver)
            ? throw new InvalidOperationException("The editor's DataAsset tracing is unavailable.")
            : null;
    }

    /// <summary>A session that fails to begin leaves Play Mode stopped, with no lifetimes or services left behind.</summary>
    [Fact]
    public void ASessionThatFailsToBeginLeavesPlayStopped()
    {
        var failing = new PlayModeService(sceneTree, new AssetDatabase(), new FailingObserverLookup(), Log.Logger);

        Assert.False(failing.Start());

        Assert.False(failing.IsActive);
        Assert.Null(failing.Lifetimes);
        Assert.Null(failing.Services);
    }
}
