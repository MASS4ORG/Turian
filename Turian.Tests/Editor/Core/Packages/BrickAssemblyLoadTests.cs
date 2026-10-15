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

        Assert.Equal(Path.Combine(root, "Dep.dll"), resolved);
    }

    /// <summary>Loading with a project root restores nothing when no brick maps a package.</summary>
    [Fact]
    public void LoadWithProjectRootSkipsEmptyMaps()
    {
        Assert.False(BrickAssemblies.Load([Brick("a.empty")], NullLogger.Instance, root));
        Assert.False(Directory.Exists(Path.Combine(root, "Bricks", BrickNuGetRestore.RestoreDirectoryName)));
    }

    ResolvedPackage Brick(string id)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0") };
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }
}
