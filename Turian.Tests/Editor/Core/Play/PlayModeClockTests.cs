namespace Turian.Tests;

/// <summary>Checks that editor play scopes configure, inject and advance their own simulation clock.</summary>
[Collection(SerialTests.Name)]
public sealed class PlayModeClockTests
{
    [TypeId("f6a6fb66-674a-4a8f-aea2-3816cd2a8f21")]
    sealed class ClockComponent : Component
    {
        /// <summary>The simulation clock injected by the play scope.</summary>
        [InjectService, JsonIgnore]
        public SimulationClock? Clock { get; private set; }

        /// <summary>The optional authoritative world injected by a game service module.</summary>
        [InjectService(Optional = true), JsonIgnore]
        public SimulationSession? Simulation { get; private set; }

        /// <summary>The interval delivered by the most recent fixed callback.</summary>
        [JsonIgnore]
        public float LastInterval { get; private set; }

        /// <inheritdoc/>
        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            LastInterval = fixedDeltaTime;
            Simulation?.Schedule(Guid.Parse("759b9dcc-52df-4ac2-875a-53f85c81b7c5"), Clock!.TickIndex + 1,
                "fixed-input", JsonSerializer.SerializeToElement(new { }));
        }
    }

    /// <summary>Project settings configure one injected clock and catch-up survives pause, step and resume.</summary>
    [Fact]
    public void PlayScope_UsesProjectTimingAndRetainsBacklog()
    {
        var settings = new AppSettings { ProjectAbsoluteDir = Path.GetTempPath() };
        var timing = new TimeSettings { FixedDeltaTime = 0.02, MaxTicksPerFrame = 2 };
        settings.Loaded.Use(timing);
        using var editor = new ServiceCollection().AddSingleton<IAppSettings>(settings).BuildServiceProvider();
        var root = new Node();
        root.AddComponent(new ClockComponent());
        root.Awake(null);
        var host = Substitute.For<IPlaySceneHost>();
        host.CurrentSceneRoot.Returns(root);
        var play = new PlayModeService(host, new AssetDatabase(), editor, NullLogger.Instance);
        try
        {
            Assert.True(play.Start("en"));
            var component = Assert.IsType<ClockComponent>(play.PlayRoot!.Components.Single());
            var clock = Assert.IsType<SimulationClock>(component.Clock);
            timing.FixedDeltaTime = 1;
            play.Tick(0.1);

            Assert.Equal(2, clock.TickIndex);
            Assert.Equal(0.02f, component.LastInterval);
            Assert.Equal(0.06, clock.AccumulatorSeconds, 12);
            play.Pause();
            Assert.True(clock.IsPaused);
            play.Tick(100);
            Assert.Equal(2, clock.TickIndex);
            play.StepFrame();
            Assert.Equal(3, clock.TickIndex);
            play.Resume();
            Assert.False(clock.IsPaused);
            play.Tick(0);
            Assert.Equal(5, clock.TickIndex);
            play.Stop();

            Assert.True(play.Start());
            var restarted = Assert.IsType<ClockComponent>(play.PlayRoot!.Components.Single());
            Assert.NotSame(clock, restarted.Clock);
            Assert.Equal(0, restarted.Clock!.TickIndex);
            Assert.Equal(1, restarted.Clock.FixedDeltaTime);
        }
        finally
        {
            play.Stop();
        }
    }

    /// <summary>Game modules override both hosts' clocks and leave the type registry when their build is disposed.</summary>
    [Fact]
    public void GameModule_OverridesTheSessionClockInBothHosts()
    {
        var directory = Directory.CreateTempSubdirectory("turian-clock-module-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Assets"));
            var settings = new BuildAppSettings { ProjectAbsoluteDir = directory };
            using var build = new BuildManager(settings, NullLogger.Instance);
            var slots = (AssemblySlotManager)typeof(BuildManager)
                .GetField("slotManager", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(build)!;
            var assemblyFile = Path.Combine(slots.InactiveSlotDirectory, "ClockGame.dll");
            var source = """
                         using Turian.Engine.Core;
                         using Microsoft.Extensions.DependencyInjection;
                         namespace Usercode;
                         [MASS4.Attributes.TypeId("cd250707-cfde-48df-b61f-1c1d29154ff0")]
                         public sealed class Game { }
                         public sealed class ClockModule : IEngineServiceModule
                         {
                             public void ConfigureServices(IServiceCollection services)
                             {
                                 services.AddSingleton(new SimulationClock(new TimeSettings
                                 { FixedDeltaTime = 0.04, MaxTicksPerFrame = 1 }));
                                 services.AddSingleton(provider => new SimulationSession(new World(),
                                     provider.GetRequiredService<SimulationClock>(), 10, "module-fixture-v1"));
                             }
                         }
                         public sealed class World : ISimulationWorld
                         {
                             long lastTick;
                             public bool ApplyCommand(SimulationCommand command, SimulationRandomStreams random) => false;
                             public void Tick(long tick, double interval, SimulationRandomStreams random) => lastTick = tick;
                             public System.Text.Json.JsonElement CaptureState() =>
                                 System.Text.Json.JsonSerializer.SerializeToElement(new { lastTick });
                             public void RestoreState(System.Text.Json.JsonElement state) =>
                                 lastTick = state.GetProperty("lastTick").GetInt64();
                         }
                         """;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var syntax = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);
            var compilation = CSharpCompilation.Create("ClockGame", [syntax], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var result = compilation.Emit(assemblyFile, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.True(slots.TrySwapAndLoad(assemblyFile));

            var root = new Node();
            root.AddComponent(new ClockComponent());
            root.Awake(null);
            var host = Substitute.For<IPlaySceneHost>();
            host.CurrentSceneRoot.Returns(root);
            using var editor = new ServiceCollection().AddSingleton<IAppSettings>(settings).AddSingleton(build)
                .BuildServiceProvider();
            CheckModuleClock(host, editor);
            using var headlessEditor = new ServiceCollection().AddSingleton<IAppSettings>(settings)
                .BuildServiceProvider();
            CheckModuleClock(host, headlessEditor);

            var sceneFile = Path.Combine(directory, "Assets", "scene.prefab");
            Serializer.Save(sceneFile, root);
            var database = new AssetDatabase();
            Assert.True(database.RegisterAsset(new Prefab { RelativePath = sceneFile }));
            database.SaveCatalog(directory);
            using var project = global::Turian.Editor.CLI.HeadlessProject.Open(settings, NullLogger.Instance, false);
            var loaded = project.LoadScene(sceneFile);
            Assert.Equal(0.04, Assert.IsType<ClockComponent>(loaded.Components.Single()).Clock!.FixedDeltaTime);

            build.Dispose();
            Assert.False(build.IsAssemblyLoaded);
            Assert.Null(build.ActiveUserAssembly);
            Assert.Empty(build.ActiveUserAssemblies);
            Assert.False(TypeRegistry.TryGetType("Usercode.Game", out _));
            TypeRegistry.Reset();
            Assert.False(TypeRegistry.TryGetType("Usercode.Game", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static void CheckModuleClock(IPlaySceneHost host, IServiceProvider editor)
    {
        var play = new PlayModeService(host, new AssetDatabase(), editor, NullLogger.Instance);
        try
        {
            Assert.True(play.Start());
            var component = Assert.IsType<ClockComponent>(play.PlayRoot!.Components.Single());
            var clock = component.Clock!;
            var simulation = Assert.IsType<SimulationSession>(component.Simulation);
            Assert.Same(clock, simulation.Clock);
            play.Tick(0.1);
            Assert.Equal(0.04, clock.FixedDeltaTime);
            Assert.Equal(1, clock.TickIndex);
            Assert.Equal(1, simulation.CaptureState().World.GetProperty("lastTick").GetInt64());
            Assert.Single(simulation.LastResults);
            Assert.Equal("fixed-input", simulation.LastResults[0].Command.Kind);
        }
        finally
        {
            play.Stop();
        }
    }
}
