namespace Turian.Tests;

/// <summary>Checks that unchanged build inputs preserve generated files and reuse either assembly slot.</summary>
[Collection(SerialTests.Name)]
public sealed class BuildReuseTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-build-reuse-").FullName;

    /// <summary>Removes this test's isolated build output.</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>Repeated project generation preserves timestamps and writes changed properties.</summary>
    [Fact]
    public void GeneratedProjectsKeepUnchangedTimestamps()
    {
        var path = Path.Combine(root, "project.csproj");
        var project = Microsoft.Build.Construction.ProjectRootElement.Create(path);
        project.Sdk = "Microsoft.NET.Sdk";
        GeneratedProject.Save(project);
        var timestamp = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(path, timestamp);
        GeneratedProject.Save(project);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        project.AddPropertyGroup().AddProperty("TargetFramework", "net10.0");
        GeneratedProject.Save(project);
        Assert.Contains("net10.0", File.ReadAllText(path));
        Assert.NotEqual(timestamp, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>Runtime asset copies preserve unchanged output files and refresh changed or missing output.</summary>
    [Fact]
    public void OutputSyncCopiesOnlyChangedFiles()
    {
        var source = Path.Combine(root, "source.bin");
        var output = Path.Combine(root, "output", "asset.bin");
        File.WriteAllText(source, "first");
        PlayOutputSync.CopyFile(source, output);
        var access = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastAccessTimeUtc(output, access);
        PlayOutputSync.CopyFile(source, output);
        Assert.Equal(access, File.GetLastAccessTimeUtc(output));
        File.WriteAllText(source, "changed content");
        PlayOutputSync.CopyFile(source, output);
        Assert.Equal("changed content", File.ReadAllText(output));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        PlayOutputSync.CopyFile(source, output);
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(output));
        File.Delete(output);
        PlayOutputSync.CopyFile(source, output);
        Assert.True(File.Exists(output));
    }

    /// <summary>A valid active-slot cache is reused when the inactive slot has no compiled output.</summary>
    [Fact]
    public async Task CompilationReusesTheOtherSlot()
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = root, Title = "Example" };
        Directory.CreateDirectory(settings.AssetsAbsoluteDir);
        settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(Guid.NewGuid());
        var active = Path.Combine(root, ".Cache", "bin", "SlotA");
        var inactive = Path.Combine(root, ".Cache", "bin", "SlotB");
        Directory.CreateDirectory(active);
        var assembly = Path.Combine(active, "Example.dll");
        File.WriteAllText(assembly, "cached output");
        var project = Path.Combine(settings.CacheAbsoluteDir, settings.SourceSubDir, "Example.csproj");
        var cache = new UserCodeCompileCache(NullLogger.Instance);
        cache.SaveManifest(root, assembly, cache.CreateManifest(settings, assembly, project));
        var compiler = new CompileUserCode(settings, NullLogger.Instance, inactive, reuseDirectory: active);
        Assert.Equal(assembly, await compiler.ExecuteAsync());
        var timestamp = File.GetLastWriteTimeUtc(project);
        Assert.Equal(assembly, await compiler.ExecuteAsync());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(project));
        Assert.Empty(Directory.GetFiles(inactive, "*.dll"));
    }

    /// <summary>Reloading an unchanged active DLL keeps the persisted slot and next compilation directory.</summary>
    [Fact]
    public void ReloadingTheActiveSlotKeepsItsIdentity()
    {
        var slots = new AssemblySlotManager(Path.Combine(root, "bin"), NullLogger.Instance);
        var path = Path.Combine(slots.ActiveSlotDirectory, "Example.dll");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Example", [CSharpSyntaxTree.ParseText("public class Example { }",
                cancellationToken: TestContext.Current.CancellationToken)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(compilation.Emit(path, cancellationToken: TestContext.Current.CancellationToken).Success);
        try
        {
            Assert.True(slots.TrySwapAndLoad(path));
            Assert.Equal(Path.GetDirectoryName(path), slots.ActiveSlotDirectory);
            Assert.True(slots.TrySwapAndLoad(path));
            var restarted = new AssemblySlotManager(Path.Combine(root, "bin"), NullLogger.Instance);
            Assert.Equal(slots.ActiveSlotDirectory, restarted.ActiveSlotDirectory);
            Assert.NotEqual(slots.ActiveSlotDirectory, restarted.InactiveSlotDirectory);
        }
        finally { slots.Unload(); }
    }

    /// <summary>Repeated playable builds reuse code while copying only changed cached assets and manifests.</summary>
    [Fact]
    public async Task PlayBuildReusesCodeAndSynchronizesAssets()
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = root, Title = "Example" };
        Directory.CreateDirectory(settings.AssetsAbsoluteDir);
        settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(Guid.NewGuid());
        var assembly = await new CompileUserCode(settings, NullLogger.Instance,
            Path.Combine(root, ".Cache", "bin", "SlotA")).ExecuteAsync();
        var typeManifest = UserCodeTypeManifest.ManifestPathFor(assembly);
        File.WriteAllText(typeManifest, "{}");
        var asset = Path.Combine(settings.CacheAbsoluteDir, "Assets", "nested", "sample.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllText(asset, "first");
        new AssetDatabase().SaveCatalog(root);
        var play = new PlayUserCode(settings, NullLogger.Instance, assembly);
        var runtime = await play.BuildAsync();
        var directory = Path.GetDirectoryName(runtime)!;
        var runtimeTime = File.GetLastWriteTimeUtc(runtime);
        Assert.Equal(runtime, await play.BuildAsync());
        Assert.Equal(runtimeTime, File.GetLastWriteTimeUtc(runtime));
        var copied = Path.Combine(directory, ".Cache", "Assets", "nested", "sample.bin");
        var assetTime = File.GetLastWriteTimeUtc(copied);
        Assert.Equal("first", File.ReadAllText(copied));
        Assert.Equal(runtime, await play.BuildAsync());
        Assert.Equal(assetTime, File.GetLastWriteTimeUtc(copied));
        File.WriteAllText(asset, "changed");
        File.WriteAllText(typeManifest, "{\"updated\":true}");
        await play.BuildAsync();
        Assert.Equal("changed", File.ReadAllText(copied));
        Assert.Equal("{\"updated\":true}", File.ReadAllText(Path.Combine(directory, UserCodeTypeManifest.FileName)));
        Assert.Equal(runtimeTime, File.GetLastWriteTimeUtc(runtime));
    }

    /// <summary>A standalone playable build generates its own type manifest when no Studio slot is supplied.</summary>
    [Fact]
    public async Task StandalonePlayBuildGeneratesItsTypeManifest()
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = root, Title = "Example" };
        Directory.CreateDirectory(settings.AssetsAbsoluteDir);
        settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(Guid.NewGuid());
        new AssetDatabase().SaveCatalog(root);
        await new CompileUserCode(settings, NullLogger.Instance,
            Path.Combine(root, ".Cache", "bin", "SlotA")).ExecuteAsync();
        var logger = Substitute.For<ILogger>();
        string runtime;
        try { runtime = await new PlayUserCode(settings, logger).BuildAsync(); }
        catch
        {
            var messages = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log")
                .Select(call => call.GetArguments()[2]?.ToString());
            throw new InvalidOperationException(string.Join(Environment.NewLine, messages));
        }
        Assert.True(File.Exists(runtime));
        Assert.True(File.Exists(UserCodeTypeManifest.ManifestPathFor(runtime)));
    }
}
