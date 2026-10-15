using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>
/// A real NuGet package in a local feed, so the brick restore path runs for real without reaching the network. It is
/// packed once per class, like the other end-to-end fixtures.
/// </summary>
public sealed class BrickNuGetFeedFixture : IAsyncLifetime
{
    /// <summary>The package id the bricks map.</summary>
    public const string PackageId = "Brick.NuGet.Probe";

    /// <summary>The version the bricks map.</summary>
    public const string PackageVersion = "1.4.0";

    /// <summary>A second version, for a test that changes a brick's map and restores again.</summary>
    public const string OtherVersion = "1.5.0";

    /// <summary>The scratch folder holding the package sources.</summary>
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"turian-brick-feed-{Guid.NewGuid():N}");

    /// <summary>The packed <c>.nupkg</c> files, the packages every feed below serves.</summary>
    public IReadOnlyList<string> Packages { get; private set; } = [];

    /// <summary>
    /// A feed holding the probe packages, of which a test may dispose to prove nothing was fetched. Each test takes
    /// its own, so a class never pulls the packages out from under its neighbours.
    /// </summary>
    /// <returns>The feed folder.</returns>
    public string NewFeed()
    {
        var feed = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"turian-brick-feed-{Guid.NewGuid():N}"))
            .FullName;
        foreach (var package in Packages)
        {
            File.Copy(package, Path.Combine(feed, Path.GetFileName(package)));
        }

        return feed;
    }

    /// <summary>Packs the probe package at both versions.</summary>
    public async ValueTask InitializeAsync()
    {
        var source = Directory.CreateDirectory(Path.Combine(Root, "source")).FullName;
        var outbox = Directory.CreateDirectory(Path.Combine(Root, "out")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, $"{PackageId}.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>{PackageId}</PackageId>
                <Version>{PackageVersion}</Version>
                <AssemblyName>{PackageId}</AssemblyName>
              </PropertyGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(source, "Probe.cs"),
            "namespace Brick.NuGet.Probe; public static class Probe { public static int Twice(int value) => value * 2; }")
            .ConfigureAwait(false);

        foreach (var version in new[] { PackageVersion, OtherVersion })
        {
            var (exitCode, output) = await Run("pack", Path.Combine(source, $"{PackageId}.csproj"), "-o", outbox,
                "-p:Version=" + version).ConfigureAwait(false);
            Assert.True(exitCode == 0, output);
        }

        Packages = Directory.GetFiles(outbox, "*.nupkg");
    }

    /// <summary>Deletes the scratch folder.</summary>
    public ValueTask DisposeAsync()
    {
        PrecastBrickFixture.DeleteTree(Root);
        return ValueTask.CompletedTask;
    }

    static async Task<(int ExitCode, string Output)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(DotnetLocator.Find())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}

/// <summary>
/// #313's acceptance criteria, run against real restores: a brick whose only dependency is a NuGet package opens
/// after restoring its map, opens again once the packages folder is warm and the feed is gone, and opens with no
/// restore at all once packed, because <c>brick pack</c> embeds the copies.
/// </summary>
/// <param name="fixture">The local package feed shared by the class.</param>
[Trait("Category", "E2E")]
public sealed class BrickNuGetLoadTests(BrickNuGetFeedFixture fixture) : IClassFixture<BrickNuGetFeedFixture>, IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-brick-nuget-load-").FullName;

    // A test instance owns one project, so every call reads the same folder.
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-brick-nuget-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>A project whose brick maps only a package restores it from the feed the project names.</summary>
    [Fact]
    public void AProjectRestoresItsBricksMap()
    {
        var brick = Brick();
        ConfigureFeed(fixture.NewFeed());

        _ = BrickAssemblies.Load([brick], NullLogger.Instance, Project);

        Assert.True(File.Exists(PackageAssembly), $"the restore did not write {PackageAssembly}");
    }

    /// <summary>The restored package's types work inside the brick's own assembly, which ships no copy of them.</summary>
    [Fact]
    public void BrickTypesWorkThroughTheRestoredPackage()
    {
        var brick = Brick();
        ConfigureFeed(fixture.NewFeed());
        _ = BrickAssemblies.Load([brick], NullLogger.Instance, Project);
        var assembly = CompileAgainst(PackageAssembly, brick);
        BrickDepsJson.Write(assembly,
            [new BrickRuntimeFile(BrickNuGetFeedFixture.PackageId, BrickNuGetFeedFixture.PackageVersion,
                $"{BrickNuGetFeedFixture.PackageId}.dll")]);

        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance, Project));
        Assert.Equal(42, Compute(assembly));
        Assert.Equal([Path.GetFileName(assembly)], BrickAssemblies.RuntimeAssemblies(brick).Select(Path.GetFileName));
    }

    /// <summary>A warm packages folder restores again after the feed is deleted, so nothing is fetched.</summary>
    [Fact]
    public void AWarmCacheRestoresWithoutTheFeed()
    {
        var brick = Brick();
        var feed = fixture.NewFeed();
        ConfigureFeed(feed);
        _ = BrickAssemblies.Load([brick], NullLogger.Instance, Project);
        var assembly = CompileAgainst(PackageAssembly, brick);
        BrickDepsJson.Write(assembly,
            [new BrickRuntimeFile(BrickNuGetFeedFixture.PackageId, BrickNuGetFeedFixture.PackageVersion,
                $"{BrickNuGetFeedFixture.PackageId}.dll")]);
        Directory.Delete(feed, recursive: true);

        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance, Project));
        Assert.Equal(42, Compute(assembly));
    }

    /// <summary>Changing a brick's map recovers: the lock file it leaves behind is dropped and rewritten.</summary>
    [Fact]
    public void AMapChangeRecoversFromAStaleLockFile()
    {
        var brick = Brick();
        ConfigureFeed(fixture.NewFeed());
        _ = BrickAssemblies.Load([brick], NullLogger.Instance, Project);
        var lockFile = Path.Combine(RestoreDirectory, BrickNuGetRestore.LockFileName);
        Assert.True(File.Exists(lockFile), "the first restore wrote no lock file");

        var manifest = PackageManifest.Load(brick.RootPath, ["gaya", ProjectPackages.HostName]);
        manifest.Nuget[BrickNuGetFeedFixture.PackageId] = BrickNuGetFeedFixture.OtherVersion;
        manifest.Save(brick.RootPath);
        var changed = new ResolvedPackage(manifest.Name, manifest, brick.RootPath, PackageOrigin.File,
            $"file:{brick.RootPath}", null, null, 1, false);

        _ = BrickAssemblies.Load([changed], NullLogger.Instance, Project);

        Assert.Equal(BrickNuGetFeedFixture.OtherVersion,
            BrickNuGetRestore.PackageDirectories(Path.Combine(Project, ".Cache", "NuGet", "packages"),
                manifest.Nuget)[0].Split(Path.DirectorySeparatorChar)[^1]);
    }

    /// <summary>A packed brick opens with no restore at all, since packing embeds the copies beside its assembly.</summary>
    [Fact]
    public void APackedBrickOpensWithoutRestoring()
    {
        var brick = Brick();
        var feed = fixture.NewFeed();
        var copied = BrickService.SyncPrecastDependencies(brick.RootPath, NullLogger.Instance, "desktop", [feed]);
        Assert.NotEmpty(copied);
        var assembly = CompileAgainst(Path.Combine(brick.RootPath, BrickAssemblies.PrecastFolder, "lib",
            $"{BrickNuGetFeedFixture.PackageId}.dll"), brick);
        var precast = new PackagePrecast { Assemblies = { Path.GetFileNameWithoutExtension(assembly) } };
        precast.BuiltWith[ProjectPackages.HostName] = ProjectPackages.EngineVersion;
        var packed = BrickService.Pack(brick.RootPath, Out, precast);

        // Nothing may be fetched: the payload carries the copy and the record that says the map is covered.
        Directory.Delete(feed, recursive: true);
        _ = Directory.CreateDirectory(Path.Combine(Project, "Assets"));
        ProjectBricks.Add(Project, brick.Manifest.Name, $"file:{packed.Path}");
        ProjectPackages.Invalidate(Project);
        var installed = ProjectPackages.ResolveOrEmpty(Project).Single(b => b.Id == brick.Manifest.Name);

        Assert.True(BrickAssemblies.Load([installed], NullLogger.Instance, Project));
        Assert.False(Directory.Exists(RestoreDirectory));
        var loaded = BrickAssemblies.RuntimeAssemblies(installed)
            .Single(path => Path.GetFileName(path) == Path.GetFileName(assembly));
        Assert.Equal(42, Compute(loaded));
    }

    string Project => project;

    string Out => Path.Combine(root, "out");

    string RestoreDirectory => Path.Combine(Project, "Bricks", BrickNuGetRestore.RestoreDirectoryName);

    string PackageAssembly => Path.Combine(Project, ".Cache", "NuGet", "packages",
        BrickNuGetFeedFixture.PackageId.ToLowerInvariant(), BrickNuGetFeedFixture.PackageVersion, "lib", "net10.0",
        $"{BrickNuGetFeedFixture.PackageId}.dll");

    // A project names its own feeds in a NuGet.config; the stub the restore runs sits beneath it, so it is honoured.
    void ConfigureFeed(string feed)
    {
        _ = Directory.CreateDirectory(Project);
        File.WriteAllText(Path.Combine(Project, BrickNuGetRestore.NuGetConfigFileName),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="probe" value="{feed}" />
              </packageSources>
            </configuration>
            """);
    }

    // A brick folder whose map holds the probe package and which ships no payload of its own.
    ResolvedPackage Brick()
    {
        var folder = BrickService.New(root, $"user.probe.b{Guid.NewGuid():N}");
        var manifest = PackageManifest.Load(folder, ["gaya", ProjectPackages.HostName]);
        manifest.Nuget[BrickNuGetFeedFixture.PackageId] = BrickNuGetFeedFixture.PackageVersion;
        manifest.Save(folder);
        return new ResolvedPackage(manifest.Name, manifest, folder, PackageOrigin.File, $"file:{folder}", null, null, 1,
            false);
    }

    // Compiles an assembly whose only dependency is <paramref name="packageAssembly"/>, into the brick's own payload.
    static string CompileAgainst(string packageAssembly, ResolvedPackage brick)
    {
        var name = $"user.probe.b{Guid.NewGuid():N}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(packageAssembly))
            .ToList();
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(
                $"namespace user.probe; public static class Scratch {{ public static int Compute(int value) => {BrickNuGetFeedFixture.PackageId}.Probe.Twice(value); }}")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var lib = Directory.CreateDirectory(Path.Combine(brick.RootPath, BrickAssemblies.PrecastFolder, "lib")).FullName;
        var assembly = Path.Combine(lib, $"{name}.dll");
        var result = compilation.Emit(assembly);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return assembly;
    }

    // Runs the brick assembly's method that calls into the package, which its load context resolves for it.
    static int Compute(string assembly)
    {
        var name = Path.GetFileNameWithoutExtension(assembly);
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(assembly);
        return (int)loaded.GetType("user.probe.Scratch")!.GetMethod("Compute")!.Invoke(null, [21])!;
    }
}
