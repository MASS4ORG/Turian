using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Runs an action the first time it is disposed, standing in for a plugin's lifetime.</summary>
sealed class Withdrawal(Action withdraw) : IDisposable
{
    Action? pending = withdraw;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
}

/// <summary>Compiles stand-ins for project editor assemblies.</summary>
static class PluginAssemblies
{
    /// <summary>A live plugin that hands its activation to the delegate the test registered as a service.</summary>
    public static string Delegating(string name) => $$"""
        public sealed class {{name}}Plugin : Gaya.Sdk.ILivePlugin
        {
            public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context)
            {
                var activate = (System.Func<Gaya.Sdk.LivePluginContext, string, System.IDisposable>)context.Services
                    .GetService(typeof(System.Func<Gaya.Sdk.LivePluginContext, string, System.IDisposable>));
                return activate(context, "{{name}}");
            }
        }
        """;

    /// <summary>Compiles <paramref name="source"/> to a file.</summary>
    public static void Emit(string path, string assemblyName, string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ILivePlugin).Assembly.Location)
            .Append(typeof(Gaya.EditorSettingAttribute).Assembly.Location)
            .Distinct()
            .Select(static reference => MetadataReference.CreateFromFile(reference));
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}

/// <summary>A live plugin bridge over real Gaya registries, fed through its assembly-set seam.</summary>
sealed class LivePluginHarness : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-live-").FullName;
    readonly AssemblyLoadContext loader = new("live-plugins", isCollectible: true);
    readonly ServiceProvider services;

    public LivePluginHarness()
    {
        Behaviour = (_, name) =>
        {
            Trace.Add($"on {name}");
            return new Withdrawal(() => Trace.Add($"off {name}"));
        };
        services = new ServiceCollection()
            .AddSingleton<Func<LivePluginContext, string, IDisposable>>((context, name) => Behaviour(context, name))
            .BuildServiceProvider();
        Context = new LivePluginContext(services, Panels, Commands, Menus) { DocumentKinds = DocumentKinds };
        Bridge = new LivePluginBridge(() => Assemblies, () => Project, Context, NullLogger.Instance, Notifications);
    }

    public PanelRegistry Panels { get; } = new();

    public CommandRegistry Commands { get; } = new();

    public MenuRegistry Menus { get; } = new();

    public DocumentKindRegistry DocumentKinds { get; } = new();

    public EditorNotifications Notifications { get; } = new();

    public LivePluginContext Context { get; }

    /// <summary>What the activations and withdrawals did, in order.</summary>
    public List<string> Trace { get; } = [];

    /// <summary>The assembly set the bridge follows; assign a new list to change it.</summary>
    public IReadOnlyList<Assembly> Assemblies { get; set; } = [];

    public string? Project { get; set; } = "first";

    /// <summary>What a delegating plugin does when activated.</summary>
    public Func<LivePluginContext, string, IDisposable> Behaviour { get; set; }

    public LivePluginBridge Bridge { get; }

    /// <summary>Loads a new assembly holding one delegating plugin, as a recompile would.</summary>
    public Assembly Load(string name) => LoadSource(PluginAssemblies.Delegating(name));

    /// <summary>Loads a new assembly compiled from <paramref name="source"/>.</summary>
    public Assembly LoadSource(string source)
    {
        var assemblyName = $"Live{Guid.NewGuid():N}";
        var path = Path.Combine(root, assemblyName + ".dll");
        PluginAssemblies.Emit(path, assemblyName, source);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return loader.LoadFromStream(stream);
    }

    /// <summary>Puts a new set in force and lets the bridge see it.</summary>
    public void Apply(params Assembly[] assemblies)
    {
        Assemblies = assemblies;
        Bridge.Sync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Bridge.Dispose();
        services.Dispose();
        loader.Unload();
        Directory.Delete(root, recursive: true);
    }
}
