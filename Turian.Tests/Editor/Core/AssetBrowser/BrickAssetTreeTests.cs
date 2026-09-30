namespace Turian.Tests;

/// <summary>Brick browser sources retain ids and distinguish installed sources from writable forks.</summary>
public sealed class BrickAssetTreeTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-brick-tree-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Store content is read-only, embedded content is writable, and payload folders are hidden.</summary>
    [Fact]
    public void ScanIncludesImportableAssetsWithStableIds()
    {
        var project = Path.Combine(root, "game");
        var installed = BrickService.New(root, "user.mateo.installed");
        var embedded = BrickService.New(Path.Combine(project, "Packages"), "user.mateo.embedded");
        Directory.CreateDirectory(Path.Combine(installed, "Precast~"));
        File.WriteAllText(Path.Combine(installed, "Precast~", "Hidden.cs"), "class Hidden {}");
        var files = new AssetFileSystem(new SettingsService(), assetImporter: null!);
        var packages = new[]
        {
            Package(installed, Gaya.Packages.PackageOrigin.Git),
            Package(embedded, Gaya.Packages.PackageOrigin.Embedded),
        };

        var entries = new BrickAssetTree(files).Scan(project, packages);

        Assert.DoesNotContain(entries, entry => entry.AbsolutePath.Contains("Precast~", StringComparison.Ordinal));
        var source = Assert.Single(entries, entry => !entry.IsDirectory &&
            entry.AbsolutePath.EndsWith("InstalledComponent.cs", StringComparison.Ordinal));
        Assert.True(source.IsReadOnly);
        Assert.Equal(Asset.Load(source.AbsolutePath + ".meta")!.Id, source.AssetMetadata!.Id);
        Assert.All(entries.Where(entry => entry.AbsolutePath.StartsWith(embedded, StringComparison.Ordinal)),
            entry => Assert.False(entry.IsReadOnly));
    }

    static Gaya.Packages.ResolvedPackage Package(string path, Gaya.Packages.PackageOrigin origin)
    {
        var manifest = Gaya.Packages.PackageManifest.Load(path, ["turian"]);
        return new Gaya.Packages.ResolvedPackage(manifest.Name, manifest, path, origin, "file:" + path,
            null, null, 1, false);
    }
}
