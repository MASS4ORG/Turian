#pragma warning disable xUnit1031 // Hosts and scene lifecycle are pumped on the same game thread throughout each test.

namespace Turian.Tests;

/// <summary>Checks async ownership in Studio and main-thread scene activation after background loading.</summary>
[Collection(SerialTests.Name)]
public sealed class GameLoopHostTests
{
    [TypeId("b30b77ab-1cc0-41ab-8991-87a781b9ca43")]
    sealed class AsyncComponent : Component
    {
        [InjectService, JsonIgnore]
        public GameLoopScheduler? Scheduler { get; private set; }

        [JsonIgnore]
        public int AwakeThread { get; private set; }

        [JsonIgnore]
        public bool Cleaned { get; private set; }

        public override void OnAwake()
        {
            AwakeThread = Environment.CurrentManagedThreadId;
            if (Scheduler is not null) Async.Run(Wait);
        }

        async ValueTask Wait(CancellationToken token)
        {
            try { await Async.Seconds(100, token); }
            finally { Cleaned = true; }
        }
    }

    /// <summary>Studio freezes waits, pumps cancellation while paused and stops each session independently.</summary>
    [Fact]
    public void Studio_InjectsSchedulerBeforeAwakeAndCancelsPausedWaits()
    {
        var root = new Node();
        root.Components.Add(new AsyncComponent());
        var host = Substitute.For<IPlaySceneHost>();
        host.CurrentSceneRoot.Returns(root);
        using var editor = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(host, new AssetDatabase(), editor, NullLogger.Instance);
        try
        {
            Assert.True(play.Start());
            var first = Assert.IsType<AsyncComponent>(play.PlayRoot!.Components.Single());
            Assert.NotNull(first.Scheduler);
            Assert.Equal(Environment.CurrentManagedThreadId, first.AwakeThread);
            play.Pause();
            play.Tick(200);
            Assert.False(first.Cleaned);
            first.Detach();
            play.Tick();
            Assert.True(first.Cleaned);
            play.Stop();
            Assert.True(first.Scheduler.IsStopped);
            Assert.True(play.Start());
            var second = Assert.IsType<AsyncComponent>(play.PlayRoot!.Components.Single());
            Assert.NotSame(first.Scheduler, second.Scheduler);
            play.Stop();
            Assert.True(second.Cleaned);
        }
        finally { play.Stop(); }
    }

    /// <summary>Scene Awake runs on the loop even when file loading starts from a worker.</summary>
    [Fact]
    public void BackgroundSceneLoad_MarshalsLifecycleOntoLoop()
    {
        var directory = Directory.CreateTempSubdirectory("turian-async-scene-").FullName;
        try
        {
            var path = Path.Combine(directory, "scene.prefab");
            var root = new Node();
            root.Components.Add(new AsyncComponent());
            Serializer.Save(path, root);
            using var scheduler = new GameLoopScheduler();
            using var services = new ServiceCollection().AddSingleton(scheduler).BuildServiceProvider();
            var manager = new SceneManager(new AssetDatabase());
            manager.BindServices(services);
            var load = Task.Run(() => manager.LoadSceneAsync(path), TestContext.Current.CancellationToken);
            var timeout = Stopwatch.StartNew();
            while (!load.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                scheduler.PumpPostedWork();
                Thread.Sleep(1);
            }
            Assert.True(load.IsCompleted);
            var scene = load.GetAwaiter().GetResult();
            var component = Assert.IsType<AsyncComponent>(scene.RootNode.Components.Single());
            Assert.Equal(Environment.CurrentManagedThreadId, component.AwakeThread);
            Assert.Same(scheduler, component.Scheduler);
            Assert.Equal(0, scheduler.FrameIndex);
            scheduler.Dispose();
            Assert.True(component.Cleaned);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>Synchronous startup activates on the loop without releasing frame waits before the first frame.</summary>
    [Fact]
    public void StartupScene_PumpsActivationWithoutAdvancingGameplay()
    {
        var directory = Directory.CreateTempSubdirectory("turian-async-startup-").FullName;
        try
        {
            var assets = Path.Combine(directory, "Assets");
            Directory.CreateDirectory(assets);
            var path = Path.Combine(assets, "scene.prefab");
            var root = new Node { Name = new string('x', 100_000) };
            root.Components.Add(new AsyncComponent());
            Serializer.Save(path, root);
            var context = new GamePlayerContext(directory, NullLogger.Instance,
                registrations => registrations.BuildServiceProvider());
            var runtime = GameRuntime.Start(context, new BufferedInputSource());
            using var scheduler = runtime.Ticker.Async;
            var prefab = new Prefab { RelativePath = path };
            Assert.True(runtime.Assets.RegisterAsset(prefab));
            runtime.Settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(prefab.Id);

            runtime.LoadStartupScene();

            var component = Assert.IsType<AsyncComponent>(runtime.StartupRoot!.Components.Single());
            Assert.Equal(Environment.CurrentManagedThreadId, component.AwakeThread);
            Assert.Equal(0, scheduler.FrameIndex);
            Assert.False(component.Cleaned);
            scheduler.Dispose();
            Assert.True(component.Cleaned);
        }
        finally { Directory.Delete(directory, true); }
    }
}
