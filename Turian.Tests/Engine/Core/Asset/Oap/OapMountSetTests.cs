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
            mounts.Readers.Select(reader => OapManifest.TryParse(reader.Manifest)!.Name));
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

        Assert.Equal(["z", "a", "base"], mounts.Readers.Select(reader => OapManifest.TryParse(reader.Manifest)!.Name));
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
            mounts.Readers.Select(reader => OapManifest.TryParse(reader.Manifest)!.Name));
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
