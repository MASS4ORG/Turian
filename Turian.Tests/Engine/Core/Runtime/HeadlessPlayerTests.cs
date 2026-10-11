namespace Turian.Tests;

/// <summary>The renderer-free game runtime: the headless player, its loop and how a launcher picks a player.</summary>
[Collection(SerialTests.Name)]
public sealed class HeadlessPlayerTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-headless-player-").FullName;

    /// <summary>Creates an empty project.</summary>
    public HeadlessPlayerTests() => Directory.CreateDirectory(Path.Combine(project, "Assets"));

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    GamePlayerContext Context() =>
        new(project, NullLogger.Instance, static services => services.BuildServiceProvider());

    /// <summary>The headless player runs until <see cref="Application.Quit"/> and returns its exit code.</summary>
    [Fact]
    public void RunsUntilQuitAndReturnsTheExitCode()
    {
        using var quit = new Timer(static _ => Application.Quit(7), null, 100, Timeout.Infinite);

        var exitCode = new HeadlessPlayer().Run(Context());

        Assert.Equal(7, exitCode);
        Assert.True(Application.IsHeadless);
        Application.BeginSession(headless: false);
        Assert.False(Application.QuitToken.IsCancellationRequested);
        Assert.Equal(0, Application.ExitCode);
    }

    /// <summary>The loop ticks the scenes on a monotonic clock until its token is cancelled.</summary>
    [Fact]
    public void LoopTicksUntilCancelled()
    {
        var ticker = new SceneTicker(new SceneManager(new AssetDatabase()));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        HeadlessPlayer.Loop(ticker, stop.Token);

        Assert.True(ticker.ElapsedSeconds > 0);
        Assert.True(ticker.Clock.TickIndex > 0);
    }

    /// <summary>
    /// A runtime opens a project without a catalog by scanning it, then reads the catalog the editor saved; it binds
    /// the scene services, including the player's own, and names the window after the project.
    /// </summary>
    [Fact]
    public void RuntimeOpensProjectsWithAndWithoutACatalog()
    {
        var scanned = GameRuntime.Start(Context(), new BufferedInputSource());
        scanned.Assets.SaveCatalog(project);
        var device = Substitute.For<IRenderDevice>();

        var cached = GameRuntime.Start(Context(), new BufferedInputSource(), services => services.AddSingleton(device));
        cached.LoadStartupScene();

        Assert.Equal(Path.GetFileName(project), cached.ProductName);
        Assert.Null(cached.StartupRoot);
        Assert.Same(device, cached.Services.GetService(typeof(IRenderDevice)));
        Assert.Same(cached.Scenes, cached.Services.GetService(typeof(ISceneManager)));
        Assert.Same(cached.Actions, cached.Services.GetService(typeof(IInputActions)));
    }

    /// <summary>A launcher runs the player its loaded render brick declares, and the headless one without any.</summary>
    [Fact]
    public void LaunchersFindTheBrickPlayer()
    {
        Assert.IsType<VulkanPlayer>(GamePlayers.Find([typeof(VulkanPlayer).Assembly]));
        Assert.Null(GamePlayers.Find([typeof(Node).Assembly]));
        Assert.IsType<VulkanPlayer>(GamePlayers.FindOrHeadless());
    }
}
