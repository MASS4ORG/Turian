using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>
/// Repeated user-code reloads and Play sessions release every load context, so no assembly stays mapped: on Windows a
/// mapped file cannot be overwritten by the next compile into its slot, nor deleted.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class PlayModeReloadCycleTests : IDisposable
{
    const int Cycles = 20;
    static readonly Guid ProbeTypeId = Guid.Parse("6f1c2a0e-5b7d-4c43-9a51-2f0d7e3b8c64");

    readonly string root = Directory.CreateTempSubdirectory("turian-reload-cycles-").FullName;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Each cycle compiles into the slot loaded two reloads before, swaps to it and plays the new code.</summary>
    [Fact]
    public void ReloadingAndPlayingRepeatedlyLeavesNoContextsOrLockedFiles()
    {
        var bin = Path.Combine(root, "bin");
        var manager = new AssemblySlotManager(bin, NullLogger.Instance);
        var references = References();
        var contexts = new List<WeakReference>();

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            contexts.Add(ReloadAndPlay(manager, cycle, references));
        }

        manager.Unload();

        Assert.All(contexts, static context => Assert.True(IsCollected(context), "A user-code load context is still referenced."));
        Directory.Delete(bin, recursive: true);
    }

    // Kept out of the test method so no reference to the loaded assembly outlives the cycle on the test's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ReloadAndPlay(AssemblySlotManager manager, int cycle, IEnumerable<MetadataReference> references)
    {
        var path = Path.Combine(manager.InactiveSlotDirectory, "UserCode.dll");
        Compile(path, cycle, references);
        Assert.True(manager.TrySwapAndLoad(path), $"Cycle {cycle} could not load its assembly.");
        return PlayOnce(manager.LoadedAssembly!, cycle);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference PlayOnce(Assembly assembly, int cycle)
    {
        TypeRegistry.ScanAssembly(assembly);
        var type = assembly.GetType("Probe")!;
        var scene = new Node { Name = "Root" };
        scene.AddComponent((Component)Activator.CreateInstance(type)!);
        scene.Awake(null);

        var database = new AssetDatabase();
        var sceneTree = new SceneTreeController(new AssetManager(), new SettingsService(), assetImporter: null!,
            assetLoader: new RuntimeAssetLoader(database), sceneManager: Substitute.For<ISceneManager>(), database: database);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, scene);
        using var editorServices = new ServiceCollection().BuildServiceProvider();
        var playMode = new PlayModeService(sceneTree, database, editorServices, NullLogger.Instance);

        Assert.True(playMode.Start(), $"Cycle {cycle} could not enter Play Mode.");
        playMode.Tick(0.016);
        playMode.Tick(0.016);
        playMode.Stop();

        Assert.Equal(cycle, type.GetProperty("Version")!.GetValue(null));
        Assert.Equal(2, type.GetProperty("Updates")!.GetValue(null));
        return new WeakReference(AssemblyLoadContext.GetLoadContext(assembly));
    }

    static bool IsCollected(WeakReference context)
    {
        for (var attempt = 0; context.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }

        return !context.IsAlive;
    }

    static MetadataReference[] References() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)),
        MetadataReference.CreateFromFile(typeof(Component).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(TypeIdAttribute).Assembly.Location),
    ];

    // Overwrites the previous build in this slot, which fails while a live load context still maps it.
    static void Compile(string path, int version, IEnumerable<MetadataReference> references)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var source = $$"""
            [MASS4.Attributes.TypeId("{{ProbeTypeId}}")]
            public sealed class Probe : Turian.Engine.Core.Component
            {
                public static int Version => {{version}};
                public static int Updates { get; private set; }
                public override void OnAwake() => Updates = 0;
                public override void OnUpdate(float deltaTime) => Updates++;
            }
            """;
        var compilation = CSharpCompilation.Create("UserCode", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
