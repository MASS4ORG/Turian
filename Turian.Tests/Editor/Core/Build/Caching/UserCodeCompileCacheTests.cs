namespace Turian.Tests.Turian.Editor.Build;

/// <summary>
/// Tests for <see cref="UserCodeCompileCache"/> hit/miss behavior.
/// Verifies that cache validation relies only on file content (hash + size), not timestamps.
/// </summary>
public sealed class UserCodeCompileCacheTests : IDisposable
{
    readonly string tempRoot;
    readonly string assetsDir;
    readonly string assemblyPath;
    readonly string csprojPath;
    readonly IBuildAppSettings settings;
    readonly UserCodeCompileCache cache;

    /// <summary>Sets up a temporary project directory and mock settings for each test.</summary>
    public UserCodeCompileCacheTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), $"TurianCacheTest_{Guid.NewGuid():N}");
        assetsDir = Path.Combine(tempRoot, "Assets");
        assemblyPath = Path.Combine(tempRoot, ".Cache", "Source", "Test.dll");
        csprojPath = Path.Combine(tempRoot, ".Cache", "Source", "Test.csproj");

        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);

        settings = Substitute.For<IBuildAppSettings>();
        settings.ProjectAbsoluteDir.Returns(tempRoot);
        settings.AssetsAbsoluteDir.Returns(assetsDir);
        settings.TargetFramework.Returns("net10.0");
        settings.TargetSdk.Returns("Microsoft.NET.Sdk");
        settings.Title.Returns("Test");
        settings.Get<PlayerSettings>().Returns(new PlayerSettings
        {
            ProductName = "Test",
            Author = "Test",
            ApplicationIdentifier = "com.test",
            Version = "1.0.0",
        });
        settings.PackageReferences.Returns([]);
        settings.TurianPackages.Returns([]);

        var logger = Substitute.For<ILogger>();
        cache = new UserCodeCompileCache(logger);
    }

    /// <summary>Removes the temporary directory created per test.</summary>
    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }

    // ── No assembly ───────────────────────────────────────────────────────────

    /// <summary>Missing output assembly causes a cache miss.</summary>
    [Fact]
    public void IsAssemblyUpToDate_NoAssembly_ReturnsFalse()
    {
        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── No manifest ───────────────────────────────────────────────────────────

    /// <summary>Assembly present but no manifest file causes a cache miss.</summary>
    [Fact]
    public void IsAssemblyUpToDate_NoManifest_ReturnsFalse()
    {
        File.WriteAllText(assemblyPath, "fake-dll");

        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── Valid cache ───────────────────────────────────────────────────────────

    /// <summary>Assembly and matching manifest produce a cache hit.</summary>
    [Fact]
    public void IsAssemblyUpToDate_ValidCache_ReturnsTrue()
    {
        WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        Assert.True(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    /// <summary>An assembly compiled against another engine module must be rebuilt.</summary>
    [Fact]
    public void IsAssemblyUpToDate_EngineChanged_ReturnsFalse()
    {
        File.WriteAllText(assemblyPath, "fake-dll");
        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        manifest.EngineModuleVersionId = Guid.NewGuid();
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── Slot rotation (#73 regression) ───────────────────────────────────────

    /// <summary>
    /// Each assembly slot keeps its own manifest so rotating between SlotA/SlotB
    /// does not cause a spurious cache miss.  Regression guard for issue #73.
    /// After both slots have been compiled once, alternating Play presses hit the cache.
    /// </summary>
    [Fact]
    public void IsAssemblyUpToDate_SlotRotation_EachSlotHitsIndependently()
    {
        WriteSourceFile("Script.cs", "class Script {}");

        var slotA = Path.Combine(tempRoot, ".Cache", "bin", "SlotA", "Test.dll");
        var slotB = Path.Combine(tempRoot, ".Cache", "bin", "SlotB", "Test.dll");

        Directory.CreateDirectory(Path.GetDirectoryName(slotA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(slotB)!);

        File.WriteAllText(slotA, "fake-dll");
        File.WriteAllText(slotB, "fake-dll");

        // Simulate: first Play → compile to SlotA, second Play → compile to SlotB (both same source)
        cache.SaveManifest(tempRoot, slotA, cache.CreateManifest(settings, slotA, csprojPath));
        cache.SaveManifest(tempRoot, slotB, cache.CreateManifest(settings, slotB, csprojPath));

        // Third Play targets SlotA again — must be a cache hit, not a recompile
        Assert.True(cache.IsAssemblyUpToDate(settings, slotA, csprojPath), "SlotA should hit after both slots compiled.");
        // Fourth Play targets SlotB again — must also hit
        Assert.True(cache.IsAssemblyUpToDate(settings, slotB, csprojPath), "SlotB should hit after both slots compiled.");
    }

    // ── Timestamp changed, content unchanged (#73 regression) ─────────────────

    /// <summary>
    /// Filesystem timestamp jitter (IDE save, git touch) must not invalidate the cache
    /// when content hash and size are unchanged.  Regression guard for issue #73.
    /// </summary>
    [Fact]
    public void IsAssemblyUpToDate_TimestampChangedContentUnchanged_ReturnsTrue()
    {
        var sourceFile = WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddSeconds(30));

        Assert.True(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── Content changed ───────────────────────────────────────────────────────

    /// <summary>Modified file content invalidates the cache.</summary>
    [Fact]
    public void IsAssemblyUpToDate_ContentChanged_ReturnsFalse()
    {
        var sourceFile = WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        File.WriteAllText(sourceFile, "class Script { void Update() {} }");

        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── File added ────────────────────────────────────────────────────────────

    /// <summary>Adding a new source file invalidates the cache.</summary>
    [Fact]
    public void IsAssemblyUpToDate_FileAdded_ReturnsFalse()
    {
        WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        WriteSourceFile("NewScript.cs", "class NewScript {}");

        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── File removed ──────────────────────────────────────────────────────────

    /// <summary>Deleting a tracked source file invalidates the cache.</summary>
    [Fact]
    public void IsAssemblyUpToDate_FileRemoved_ReturnsFalse()
    {
        var file1 = WriteSourceFile("Script.cs", "class Script {}");
        WriteSourceFile("Helper.cs", "class Helper {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        File.Delete(file1);

        Assert.False(cache.IsAssemblyUpToDate(settings, assemblyPath, csprojPath));
    }

    // ── Invalidate ────────────────────────────────────────────────────────────

    /// <summary>Invalidate removes the manifest file from disk.</summary>
    [Fact]
    public void Invalidate_DeletesManifest()
    {
        WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);
        cache.SaveManifest(tempRoot, assemblyPath, manifest);

        Assert.True(File.Exists(UserCodeCompileCache.GetManifestPath(tempRoot, assemblyPath)));

        cache.Invalidate(tempRoot, assemblyPath);

        Assert.False(File.Exists(UserCodeCompileCache.GetManifestPath(tempRoot, assemblyPath)));
    }

    // ── CreateManifest ────────────────────────────────────────────────────────

    /// <summary>CreateManifest populates the expected fields from settings and source files.</summary>
    [Fact]
    public void CreateManifest_SetsExpectedFields()
    {
        WriteSourceFile("Script.cs", "class Script {}");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);

        Assert.Equal(tempRoot, manifest.ProjectPath);
        Assert.Equal("net10.0", manifest.TargetFramework);
        Assert.Single(manifest.SourceFiles);
        Assert.NotNull(manifest.SourceFingerprint);
        Assert.NotNull(manifest.SettingsFingerprint);
    }

    /// <summary>.cs files inside the .Cache directory are excluded from the source manifest.</summary>
    [Fact]
    public void CreateManifest_ExcludesFilesInsideCacheDirectory()
    {
        WriteSourceFile("Script.cs", "class Script {}");
        var cacheCs = Path.Combine(tempRoot, ".Cache", "generated.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheCs)!);
        File.WriteAllText(cacheCs, "// generated");
        File.WriteAllText(assemblyPath, "fake-dll");

        var manifest = cache.CreateManifest(settings, assemblyPath, csprojPath);

        Assert.All(manifest.SourceFiles, f =>
            Assert.DoesNotContain(".Cache", f.RelativePath, StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(assetsDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
