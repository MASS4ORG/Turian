#pragma warning disable xUnit1031
namespace Turian.Tests;

/// <summary>A running game holds one gameplay session from start to dispose, restartable from game code.</summary>
[Collection(SerialTests.Name)]
public sealed class GameRuntimeSessionTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-runtime-session-").FullName;

    /// <summary>Creates an empty project.</summary>
    public GameRuntimeSessionTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(project, recursive: true);

    GamePlayerContext Context() => new(project, NullLogger.Instance, static services => services.BuildServiceProvider());

    /// <summary>The session begins with the runtime, is replaced by a restart and ends with the runtime.</summary>
    [Fact]
    public void ARuntimeRunsOneSessionUntilRestartedOrDisposed()
    {
        var runtime = GameRuntime.Start(Context(), new BufferedInputSource());
        var first = runtime.Lifetimes.DataAssets;
        Assert.NotNull(first);
        Assert.Same(first, runtime.Services.GetService<DataAssetSession>());
        Assert.Same(runtime.Lifetimes, runtime.Services.GetService<ISessionLifetime>());

        runtime.Lifetimes.Restart();
        runtime.Ticker.StepFrame();
        Assert.NotNull(runtime.Lifetimes.DataAssets);
        Assert.NotSame(first, runtime.Lifetimes.DataAssets);
        Assert.Empty(runtime.Scenes.LoadedScenes);

        // A scene that is not in the catalog fails to load; the failure is logged and the new session still runs.
        runtime.Lifetimes.Restart(Guid.NewGuid());
        runtime.Ticker.StepFrame();
        Assert.NotNull(runtime.Lifetimes.DataAssets);

        runtime.Dispose();
        Assert.Null(runtime.Lifetimes.Session);
    }

    [TypeId("5d3c1c43-7f4e-4f0b-9a55-3a0d7d6b2f11")]
    sealed class Coins : DataAsset
    {
        public int Value { get; set; } = 3;
    }

    /// <summary>A loaded DataAsset joins the running session, and joins the next one when loaded again after a restart.</summary>
    [Fact]
    public void LoadedDataAssetsJoinTheRunningSession()
    {
        var source = Path.Combine(project, "Assets", "Coins.dataasset");
        Serializer.Save<DataAsset>(source, new Coins());
        var metadata = new DataAssetAsset { RelativePath = "Assets/Coins.dataasset" };
        Serializer.Save($"{source}.meta", metadata);
        using var runtime = GameRuntime.Start(Context(), new BufferedInputSource());
        var loader = runtime.Services.GetRequiredService<IAssetLoader>();

        var coins = loader.LoadContentAsync<Coins>(metadata.Id).GetAwaiter().GetResult();
        Assert.NotNull(coins);
        Assert.Same(coins, runtime.Lifetimes.DataAssets!.Find(metadata.Id));

        runtime.Lifetimes.Restart();
        runtime.Ticker.StepFrame();
        Assert.Null(runtime.Lifetimes.DataAssets!.Find(metadata.Id));

        Assert.Same(coins, loader.LoadContentAsync<Coins>(metadata.Id).GetAwaiter().GetResult());
        Assert.Same(coins, runtime.Lifetimes.DataAssets!.Find(metadata.Id));
    }

    sealed class Score : ISubsystem
    {
        public void Start() { }
        public void Stop() { }
    }

    /// <summary>An exported game's generated registrations compose its session subsystems.</summary>
    [Fact]
    public void AnExportedGameComposesItsGeneratedSubsystems()
    {
        ExportRuntime.ConfigureServices = static services =>
            services.AddSubsystem(LifetimeScope.Session, static _ => new Score());
        try
        {
            using var runtime = GameRuntime.Start(Context(), new BufferedInputSource());

            Assert.NotNull(runtime.Lifetimes.Session!.Get<Score>());
            Assert.Same(runtime.Lifetimes.Session!.Get<Score>(), runtime.Services.GetService<Score>());
        }
        finally
        {
            ExportRuntime.ConfigureServices = null;
        }
    }
}
