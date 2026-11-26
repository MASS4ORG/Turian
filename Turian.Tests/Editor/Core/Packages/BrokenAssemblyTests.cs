using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>An assembly whose module initializer needs something missing is skipped and logged instead of ending the process.</summary>
public sealed class BrokenAssemblyTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-broken-assembly-").FullName;
    readonly List<string> warnings = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // The default context keeps loaded files mapped until the process ends.
        }
    }

    /// <summary>The initializer failure is caught, names the missing assembly once, and the assembly is marked.</summary>
    [Fact]
    public void FailingInitializerIsCaughtAndLoggedOnce()
    {
        var assembly = LoadFailing(new AssemblyLoadContext(null, isCollectible: true), "InitializerProbe");
        var logger = new Collector(warnings);

        Assert.False(BrokenAssemblies.Initialize(assembly, logger));
        Assert.False(BrokenAssemblies.Initialize(assembly, logger));

        Assert.True(BrokenAssemblies.IsBroken(assembly));
        Assert.Contains("Missing.Dependency", Assert.Single(warnings));
    }

    /// <summary>A working assembly initializes normally.</summary>
    [Fact]
    public void HealthyAssemblyInitializes()
    {
        Assert.True(BrokenAssemblies.Initialize(typeof(BrokenAssemblyTests).Assembly, new Collector(warnings)));
        Assert.Empty(warnings);
    }

    /// <summary>Loading a precast brick whose initializer fails does not throw, registers none of its types and loads nothing.</summary>
    [Fact]
    public void LoadSkipsBrickWithFailingInitializer()
    {
        var name = $"FailingBrick{Guid.NewGuid():N}";
        var folder = Directory.CreateDirectory(Path.Combine(root, "brick")).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(folder, BrickAssemblies.PrecastFolder, "lib")).FullName;
        File.WriteAllBytes(Path.Combine(lib, $"{name}.dll"), Compile(name));
        var manifest = new PackageManifest { Name = "user.failing.brick", Version = SemanticVersion.Parse("1.0.0") };
        var brick = new ResolvedPackage(manifest.Name, manifest, folder, PackageOrigin.Link, $"file:{folder}", null, null, 1, false);

        var loaded = BrickAssemblies.Load([brick], new Collector(warnings));

        Assert.False(loaded);
        Assert.Contains("Missing.Dependency", Assert.Single(warnings));
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);
        Assert.True(BrokenAssemblies.IsBroken(assembly));
    }

    static byte[] Compile(string name)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText("""
                using System.IO;
                using System.Runtime.CompilerServices;
                static class Init
                {
                    [ModuleInitializer]
                    internal static void Run() => throw new FileNotFoundException("missing", "Missing.Dependency, Version=1.0.0.0");
                }
                """)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(compilation.Emit(image).Success);
        return image.ToArray();
    }

    static Assembly LoadFailing(AssemblyLoadContext context, string name) =>
        context.LoadFromStream(new MemoryStream(Compile(name)));

    sealed class Collector(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
