namespace Turian.Tests;

/// <summary>Package requirements and overlay precedence share one mount order.</summary>
public sealed class OapMountSetTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-oap-mount-{Guid.NewGuid():N}");
    readonly Guid assetId = Guid.NewGuid();

    /// <summary>Creates an isolated package directory.</summary>
    public OapMountSetTests() => Directory.CreateDirectory(Path.Combine(root, "overlays"));

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Dependents override their requirements even when filenames would put them below.</summary>
    [Fact]
    public void DependenciesDeterminePrecedenceBeforeFilename()
    {
        var basePath = Package("base.oap", "base", "base");
        Package("overlays/z-required.oap", "dependency", "required");
        Package("overlays/a-dependent.oap", "dependent", "dependent", "dependency");

        var mounts = OapMountSet.ForBasePackage(basePath)!;

        Assert.Equal(["dependent", "dependency", "base"],
            mounts.Readers.Select(static reader => OapManifest.TryParse(reader.Manifest)!.Name));
        Assert.True(mounts.TryResolveById(assetId, out var winner, out var entry));
        Assert.Equal("dependent", Encoding.UTF8.GetString(winner.ReadAsset(entry)));
    }

    /// <summary>Missing or cyclic package requirements fail before a partial mount is published.</summary>
    [Fact]
    public void MissingRequirementAndCycleFail()
    {
        var basePath = Package("base.oap", "base", "base");
        Package("overlays/a.oap", "a", "a", "absent");
        Assert.Contains("absent", Assert.Throws<InvalidOperationException>(
            () => OapMountSet.ForBasePackage(basePath)).Message, StringComparison.Ordinal);

        Package("overlays/a.oap", "a", "a", "b");
        Package("overlays/b.oap", "b", "b", "a");
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(
            () => OapMountSet.ForBasePackage(basePath)).Message, StringComparison.Ordinal);
    }

    /// <summary>Ambiguous package names cannot silently bind a dependent to either one.</summary>
    [Fact]
    public void DuplicatePackageNamesFail()
    {
        var basePath = Package("base.oap", "base", "base");
        Package("overlays/a.oap", "same", "a");
        Package("overlays/b.oap", "same", "b");

        Assert.Contains("same", Assert.Throws<InvalidOperationException>(
            () => OapMountSet.ForBasePackage(basePath)).Message, StringComparison.Ordinal);
    }

    /// <summary>The base stays lowest; independent overlays use their stable filename order.</summary>
    [Fact]
    public void IndependentOverlaysUseFilenameOrder()
    {
        var basePath = Package("base.oap", "base", "base");
        Package("overlays/z.oap", "z", "z");
        Package("overlays/a.oap", "a", "a");

        var mounts = OapMountSet.ForBasePackage(basePath)!;

        Assert.Equal(["z", "a", "base"], mounts.Readers.Select(static reader => OapManifest.TryParse(reader.Manifest)!.Name));
        Assert.True(mounts.TryResolveById(assetId, out var reader, out var entry));
        Assert.Equal("z", Encoding.UTF8.GetString(reader.ReadAsset(entry)));
    }

    /// <summary>A base depending on an overlay contradicts its position below every overlay.</summary>
    [Fact]
    public void BaseCannotRequireOverlay()
    {
        var basePath = Package("base.oap", "base", "base", "overlay");
        Package("overlays/overlay.oap", "overlay", "overlay");

        Assert.Contains("Base OAP", Assert.Throws<InvalidOperationException>(
            () => OapMountSet.ForBasePackage(basePath)).Message, StringComparison.Ordinal);
    }

    /// <summary>The order of requirements never changes precedence between otherwise unrelated packages.</summary>
    [Fact]
    public void RequirementListOrderDoesNotChooseConflictWinner()
    {
        var basePath = Package("base.oap", "base", "base");
        Package("overlays/z.oap", "z", "z");
        Package("overlays/y.oap", "y", "y");
        Package("overlays/x.oap", "x", "x");
        Package("overlays/a.oap", "a", "a", "y", "z");

        var mounts = OapMountSet.ForBasePackage(basePath)!;

        Assert.Equal(["x", "a", "z", "y", "base"],
            mounts.Readers.Select(static reader => OapManifest.TryParse(reader.Manifest)!.Name));
    }

    /// <summary>Malformed package requirements cannot silently disable dependency checks.</summary>
    [Theory]
    [InlineData("""{"name":"bad","requires":"missing"}""")]
    [InlineData("""{"name":"bad","requires":[42]}""")]
    [InlineData("""{"name":"bad","requires":[" "]}""")]
    public void MalformedRequirementsFail(string manifest)
    {
        var basePath = Package("base.oap", "base", "base");
        PackageWithManifest("overlays/bad.oap", "bad", manifest);

        Assert.Contains("manifest", Assert.Throws<InvalidOperationException>(
            () => OapMountSet.ForBasePackage(basePath)).Message, StringComparison.Ordinal);
    }

    /// <summary>An unchanged package reuses the cached mount; a replaced one rebuilds it while the old mount keeps
    /// reading the bytes it opened.</summary>
    [Fact]
    public void ReplacedPackageRebuildsMountAndOldMountKeepsItsBytes()
    {
        var basePath = Package("base.oap", "base", "old");
        using var first = OapMountSet.ForBasePackage(basePath)!;
        Assert.Same(first, OapMountSet.ForBasePackage(basePath));

        Package("base.oap", "base", "new-content");
        File.SetLastWriteTimeUtc(basePath, DateTime.UtcNow.AddMinutes(1));
        using var second = OapMountSet.ForBasePackage(basePath)!;

        Assert.NotSame(first, second);
        Assert.Throws<InvalidDataException>(first.EnsureUnchanged);
        second.EnsureUnchanged();
        Assert.Equal("old", ReadWinner(first));
        Assert.Equal("new-content", ReadWinner(second));
    }

    /// <summary>A new overlay changes the package list, so the cached mount is rebuilt with it.</summary>
    [Fact]
    public void AddedOverlayRebuildsMount()
    {
        var basePath = Package("base.oap", "base", "base");
        using var first = OapMountSet.ForBasePackage(basePath)!;

        Package("overlays/patch.oap", "patch", "patch");
        using var second = OapMountSet.ForBasePackage(basePath)!;

        Assert.Single(first.Readers);
        Assert.Equal("patch", ReadWinner(second));
    }

    /// <summary>A missing base package yields no mount.</summary>
    [Fact]
    public void MissingBaseReturnsNull() => Assert.Null(OapMountSet.ForBasePackage(Path.Combine(root, "none.oap")));

    /// <summary>Mounting needs only the content contract, so a non-file location such as APK assets works.</summary>
    [Fact]
    public void MountsFromNonFileContentDirectory()
    {
        var directory = new MemoryContentDirectory();
        directory.Add("data/game.oap", PackageBytes("base", "base"));
        directory.Add("data/overlays/mod.oap", PackageBytes("mod", "mod"));

        using var mounts = OapMountSet.ForBasePackage(directory, "data/game.oap")!;

        Assert.Equal(["mod", "base"], mounts.Readers.Select(static reader => OapManifest.TryParse(reader.Manifest)!.Name));
        Assert.True(mounts.TryResolveById(assetId, out var reader, out var entry));
        using var stream = reader.OpenAssetStream(entry);
        Assert.Equal("mod", new StreamReader(stream).ReadToEnd());
        Assert.Same(mounts, OapMountSet.ForBasePackage(directory, "data/game.oap"));
    }

    /// <summary>A package that fails to parse releases every source the mount opened.</summary>
    [Fact]
    public void InvalidPackageDisposesOpenedSources()
    {
        var directory = new MemoryContentDirectory();
        directory.Add("game.oap", PackageBytes("base", "base"));
        directory.Add("overlays/bad.oap", [1, 2, 3]);

        Assert.Throws<OapTruncatedException>(() => OapMountSet.ForBasePackage(directory, "game.oap"));
        Assert.All(directory.Opened, source => Assert.True(source.Disposed));
    }

    /// <summary>An overlay tombstone removes the base asset by id and by path; a higher overlay restores it.</summary>
    [Fact]
    public void TombstoneHidesLowerPackagesUntilAHigherOneAddsTheAssetBack()
    {
        var basePath = Package("base.oap", "base", "base");
        var patch = new OapWriter();
        patch.AddTombstone(assetId, "asset.txt");
        patch.SetManifest(JsonSerializer.Serialize(new { name = "patch", requires = new[] { "base" } }));
        patch.WriteToFile(Path.Combine(root, "overlays", "a-patch.oap"));

        using (var mounts = OapMountSet.ForBasePackage(basePath)!)
        {
            Assert.False(mounts.TryResolveById(assetId, out _, out _));
            Assert.False(mounts.TryResolveByPath("asset.txt", out _, out _));
        }

        Package("overlays/b-mod.oap", "mod", "restored", "patch");
        using var restored = OapMountSet.ForBasePackage(basePath)!;
        Assert.Equal("restored", ReadWinner(restored));
    }

    string ReadWinner(OapMountSet mounts)
    {
        Assert.True(mounts.TryResolveById(assetId, out var reader, out var entry));
        using var stream = reader.OpenAssetStream(entry);
        return new StreamReader(stream).ReadToEnd();
    }

    byte[] PackageBytes(string name, string content)
    {
        var writer = new OapWriter();
        writer.Add(assetId, Encoding.UTF8.GetBytes(content), "asset.txt");
        writer.SetManifest(JsonSerializer.Serialize(new { name }));
        return writer.Serialize();
    }

    string Package(string relativePath, string name, string content, params string[] requires) =>
        PackageWithManifest(relativePath, content, JsonSerializer.Serialize(new { name, requires }));

    string PackageWithManifest(string relativePath, string content, string manifest)
    {
        var path = Path.Combine(root, relativePath);
        var writer = new OapWriter();
        writer.Add(assetId, Encoding.UTF8.GetBytes(content), "asset.txt");
        writer.SetManifest(manifest);
        writer.WriteToFile(path);
        return path;
    }
}
