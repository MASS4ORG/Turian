using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Unloaded user assemblies are not kept alive by the engine's and editor's static type caches.</summary>
[Collection(SerialTests.Name)]
public sealed class CollectibleAssembliesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-collectible-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* an assembly file can stay mapped until the context is finalized */ }
        catch (UnauthorizedAccessException) { /* same, on Windows */ }
    }

    /// <summary>A context whose types went through every engine cache is collected once released and unloaded.</summary>
    [Fact]
    public void ReleasedContextIsCollected()
    {
        var typeId = Guid.NewGuid();
        var context = UseReleaseAndUnload(Compile(Path.Combine(root, "plain"), typeId), typeId);

        Assert.True(IsCollected(context), "The released load context is still referenced.");
        Assert.False(TypeRegistry.TryGetType(typeId, out _));
    }

    /// <summary>Unloading user code through the slot manager releases the caches it populated, the editor's too.</summary>
    [Fact]
    public void SlotManagerUnloadCollectsUserCode()
    {
        var typeId = Guid.NewGuid();
        var manager = new AssemblySlotManager(Path.Combine(root, "bin"), NullLogger.Instance);
        var context = LoadUseAndUnload(manager, Compile(Path.Combine(root, "build"), typeId), typeId);

        Assert.True(IsCollected(context), "The unloaded user-code context is still referenced.");
        Assert.Null(manager.LoadedAssembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference UseReleaseAndUnload(string path, Guid typeId)
    {
        var context = new AssemblyLoadContext($"probe-{typeId:N}", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(path);
        TypeRegistry.ScanAssembly(assembly);
        var type = Exercise(assembly, typeId);
        GeneratedSerializers.Register(type, new GeneratedSerializer(() => (IdObject)Activator.CreateInstance(type)!,
            static (_, _, _) => { }, static (_, _, _) => false, static (_, _, _) => false, static _ => null));

        CollectibleAssemblies.Release(context.Assemblies.ToArray());
        Assert.False(TypeRegistry.TryGetType(typeId, out _));
        Assert.False(GeneratedSerializers.TryGet(type, out _));
        Assert.True(CollectibleAssemblies.IsReleased(typeof(List<>).MakeGenericType(type).MakeArrayType()));
        context.Unload();
        return new WeakReference(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference LoadUseAndUnload(AssemblySlotManager manager, string path, Guid typeId)
    {
        Assert.True(manager.TrySwapAndLoad(path));
        var assembly = manager.LoadedAssembly!;
        TypeRegistry.ScanAssembly(assembly);
        var type = Exercise(assembly, typeId);
        ObjectState.Capture((IdObject)Activator.CreateInstance(type)!);
        var context = new WeakReference(AssemblyLoadContext.GetLoadContext(assembly));
        manager.Unload();
        return context;
    }

    // Runs the type through the registry, the serializer, reference resolution and service injection.
    static Type Exercise(Assembly assembly, Guid typeId)
    {
        var type = assembly.GetType("Probe")!;
        Assert.Same(type, TypeRegistry.GetTypeOrThrow(typeId));
        var probe = (Component)Activator.CreateInstance(type)!;
        SceneServiceInjector.Inject(probe, new ServiceCollection().BuildServiceProvider(), allowMissingServices: true);
        var loaded = Serializer.LoadData<Component>(Serializer.Serialize<Component>(probe));
        Assert.IsType(type, loaded);
        return type;
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

    static string Compile(string directory, Guid typeId)
    {
        Directory.CreateDirectory(directory);
        var source = $$"""
            [MASS4.Attributes.TypeId("{{typeId}}")]
            public sealed class Probe : Turian.Engine.Core.Component
            {
                public int Value { get; set; } = 7;
                public Turian.Engine.Core.Component? Other { get; set; }
                [Turian.Engine.Core.InjectService(Optional = true), System.Text.Json.Serialization.JsonIgnore]
                public System.IServiceProvider? Services { get; set; }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TypeIdAttribute).Assembly.Location));
        var name = $"Probe{typeId:N}";
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(directory, $"{name}.dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}
