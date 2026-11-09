using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Brick dependency probing: restored package folders and per-brick dependency resolvers.</summary>
public sealed class BrickAssemblyLoadTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-brick-load-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Unknown assemblies resolve to nothing.</summary>
    [Fact]
    public void UnknownAssembliesResolveToNull()
    {
        Assert.Null(BrickAssemblies.ResolveDependency(new AssemblyName($"NoSuchAssembly{Guid.NewGuid():N}")));
    }

    /// <summary>A file in a probed package folder resolves by simple name.</summary>
    [Fact]
    public void PackageFoldersResolveBySimpleName()
    {
        var lib = Directory.CreateDirectory(Path.Combine(root, "packages", "x", "1.4.0", "lib", "net10.0")).FullName;
        File.Copy(typeof(BrickNuGet).Assembly.Location, Path.Combine(lib, "X.dll"));
        BrickAssemblies.RegisterProbing([Brick("a.prober")], [Path.Combine(root, "packages", "x", "1.4.0")]);

        Assert.Equal(Path.Combine(lib, "X.dll"), BrickAssemblies.ResolveDependency(new AssemblyName("X")));
    }

    /// <summary>A package that only targets an older framework, as Bepu ships net8.0, still resolves from its cache.</summary>
    [Fact]
    public void PackageFoldersResolveOlderFrameworks()
    {
        var name = $"Net8Only{Guid.NewGuid():N}";
        var package = Path.Combine(root, "packages", name, "2.5.0");
        var lib = Directory.CreateDirectory(Path.Combine(package, "lib", "net8.0")).FullName;
        File.Copy(typeof(BrickNuGet).Assembly.Location, Path.Combine(lib, $"{name}.dll"));
        BrickAssemblies.RegisterProbing([Brick("a.prober")], [package]);

        Assert.Equal(Path.Combine(lib, $"{name}.dll"), BrickAssemblies.ResolveDependency(new AssemblyName(name)));
    }

    /// <summary>A brick's sidecar deps.json resolves its restored files through a dependency resolver.</summary>
    [Fact]
    public void BrickDepsJsonResolvesRestoredFiles()
    {
        var assembly = Path.Combine(root, "A.dll");
        File.Copy(typeof(BrickNuGet).Assembly.Location, assembly);
        File.Copy(typeof(Component).Assembly.Location, Path.Combine(root, "Dep.dll"));
        BrickDepsJson.Write(assembly, [new BrickRuntimeFile("Dep", "3.2.0", "Dep.dll")]);

        var resolved = new AssemblyDependencyResolver(assembly).ResolveAssemblyToPath(new AssemblyName("Dep"));

        // The resolver returns the real path, which on macOS goes through the /var -> /private/var symlink.
        Assert.NotNull(resolved);
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "Dep.dll")), File.ReadAllBytes(resolved));
        Assert.Equal("Dep.dll", Path.GetFileName(resolved));
    }

    /// <summary>Loading with a project root restores nothing when no brick maps a package.</summary>
    [Fact]
    public void LoadWithProjectRootSkipsEmptyMaps()
    {
        Assert.False(BrickAssemblies.Load([Brick("a.empty")], NullLogger.Instance, root));
        Assert.False(Directory.Exists(Path.Combine(root, "Bricks", BrickNuGetRestore.RestoreDirectoryName)));
    }

    /// <summary>A brick requiring a newer shared library is rejected before it can fail during rendering.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task LoadChecksAlreadyLoadedDependencyVersions(int hostVersion, bool compatible)
    {
        var name = "Shared" + Guid.NewGuid().ToString("N");
        var host = Compile(name, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{hostVersion}}.0.0.0")]
            public sealed class SharedType { }
            """);
        var hostPath = Path.Combine(root, name + ".dll");
        File.WriteAllBytes(hostPath, host);
        var required = Compile(name, """
            [assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
            public sealed class SharedType { }
            """);
        var brickName = "Consumer" + Guid.NewGuid().ToString("N");
        var brick = Brick("test." + brickName.ToLowerInvariant());
        var lib = Directory.CreateDirectory(Path.Combine(BrickAssemblies.PrecastDirectory(brick), "lib")).FullName;
        File.WriteAllBytes(Path.Combine(lib, brickName + ".dll"), Compile(brickName,
            "public sealed class Consumer { public SharedType Value; }", MetadataReference.CreateFromImage(required)));

        await RunDependencyProbe(hostPath, brick, brickName, compatible);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == brickName);
    }

    static byte[] Compile(string name, string source, params MetadataReference[] extraReferences)
        => Compile(name, source, OutputKind.DynamicallyLinkedLibrary, extraReferences);

    static byte[] Compile(string name, string source, OutputKind outputKind, params MetadataReference[] extraReferences)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .Concat(extraReferences);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(outputKind));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    async Task RunDependencyProbe(string hostPath, ResolvedPackage brick, string consumerName, bool compatible)
    {
        var assembly = Path.Combine(root, "DependencyProbe.dll");
        File.WriteAllBytes(assembly, Compile("DependencyProbe", DependencyProbeSource, OutputKind.ConsoleApplication));
        File.Copy(Path.ChangeExtension(typeof(BrickAssemblyLoadTests).Assembly.Location, ".runtimeconfig.json"),
            Path.ChangeExtension(assembly, ".runtimeconfig.json"));
        var start = new ProcessStartInfo(DotnetLocator.Find())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 { assembly, AppContext.BaseDirectory, hostPath, brick.RootPath, brick.Id, consumerName, compatible.ToString() })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var token = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(process.ExitCode == 0, $"Dependency probe exited with {process.ExitCode}: {await output}{await error}");
    }

    // Default-context assemblies retain Windows file mappings until process exit, so the parent owns cleanup.
    const string DependencyProbeSource = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Reflection;
        using System.Runtime.CompilerServices;
        using System.Runtime.Loader;
        using MASS4.Bricks;
        using Microsoft.Extensions.Logging;
        using Turian.Editor.Core;

        static class DependencyProbe
        {
            static int Main(string[] args)
            {
                AssemblyLoadContext.Default.Resolving += (_, name) =>
                {
                    var path = Path.Combine(args[0], name.Name + ".dll");
                    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                };
                return Check(args);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            static int Check(string[] args)
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(args[1]);
                var manifest = new PackageManifest { Name = args[3], Version = SemanticVersion.Parse("0.1.0") };
                var brick = new ResolvedPackage(args[3], manifest, args[2], PackageOrigin.Builtin,
                    "builtin:" + args[3], null, null, 1, false);
                var expected = bool.Parse(args[5]);
                var logger = new ProbeLogger();
                var loaded = BrickAssemblies.Load(new[] { brick }, logger);
                var present = AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == args[4]);
                Console.WriteLine($"loaded={loaded}; present={present}; rejected={logger.Rejected}");
                if (loaded != expected) return 1;
                if (present != expected) return 2;
                return !expected && !logger.Rejected ? 3 : 0;
            }

            sealed class ProbeLogger : ILogger
            {
                public bool Rejected;
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel level) => true;
                public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    Rejected |= exception is FileLoadException
                        && exception.Message.Contains("host already loaded", StringComparison.Ordinal);
                }
            }
        }
        """;

    ResolvedPackage Brick(string id)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0") };
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }
}
