namespace Turian.Tests;

/// <summary>What a Turian project does with bricks: create, verify, pack, install.</summary>
public sealed class BrickServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-brick-service-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickServiceTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A new brick passes verification and names its assembly after the id.</summary>
    [Fact]
    public void NewBrickIsSound()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");

        Assert.Empty(BrickVerifier.Verify(folder));
        Assert.True(File.Exists(Path.Combine(folder, "Runtime", "Mateo.Inventory.dataasset")));
        Assert.DoesNotContain("keywords", File.ReadAllText(Path.Combine(folder, "package.json")), StringComparison.Ordinal);
        Assert.Throws<PackageException>(() => BrickService.New(root, "user.mateo.inventory"));
        Assert.Throws<PackageException>(() => BrickService.New(root, "Inventory"));
    }

    /// <summary>A folder that declares no bricks is not given a Bricks folder with an empty lock file.</summary>
    [Fact]
    public void ResolvingAFolderWithoutBricksWritesNothing()
    {
        var folder = Path.Combine(root, "plain");
        Directory.CreateDirectory(folder);

        var resolution = ProjectPackages.Resolve(folder);

        Assert.Empty(resolution.Packages);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    /// <summary>A failed declaration rolls back only the manifest and preserves embedded sources.</summary>
    [Fact]
    public void FailedAddDoesNotRemoveEmbeddedSources()
    {
        var project = Path.Combine(root, "game");
        var embedded = BrickService.New(Path.Combine(project, "Bricks"), "user.mateo.inventory");
        var manifest = PackageManifest.Load(embedded, ["turian"]);
        manifest.Dependencies["user.mateo.missing"] = "file:../missing";
        manifest.Save(embedded);

        Assert.Throws<PackageException>(() => BrickService.Add(project, manifest.Name, "file:../other"));

        Assert.True(File.Exists(Path.Combine(embedded, "Runtime", "InventoryComponent.cs")));
        Assert.Empty(ProjectManifest.Load(project).Manifest.Dependencies);
    }

    /// <summary>Verification names missing metas, duplicate ids and orphans, and ignores <c>~</c> folders.</summary>
    [Fact]
    public void VerifierFindsAssetProblems()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        File.WriteAllText(Path.Combine(folder, "Runtime", "NoMeta.txt"), "x");
        File.WriteAllText(Path.Combine(folder, "Runtime", "Orphan.png.meta"), """{ "Id": "00000000-0000-0000-0000-000000000000" }""");
        var scriptMeta = File.ReadAllText(Path.Combine(folder, "Runtime", "InventoryComponent.cs.meta"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Copy.cs"), "class Copy {}");
        File.WriteAllText(Path.Combine(folder, "Runtime", "Copy.cs.meta"), scriptMeta);
        Directory.CreateDirectory(Path.Combine(folder, "Samples~"));
        File.WriteAllText(Path.Combine(folder, "Samples~", "Free.txt"), "no meta needed");

        var issues = BrickVerifier.Verify(folder);

        Assert.Contains(issues, i => i.Contains("NoMeta.txt has no .meta", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Orphan.png.meta has no asset", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("Orphan.png.meta has no valid Id", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("reuses the id", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Contains("Free.txt", StringComparison.Ordinal));
    }

    /// <summary>A brick that fails verification is not packed; a sound one is installed into a project from its file.</summary>
    [Fact]
    public void PackedBricksInstall()
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        File.WriteAllText(Path.Combine(folder, "Runtime", "NoMeta.txt"), "x");
        Assert.Throws<PackageException>(() => BrickService.Pack(folder, Path.Combine(root, "out")));
        File.Delete(Path.Combine(folder, "Runtime", "NoMeta.txt"));

        var packed = BrickService.Pack(folder, Path.Combine(root, "out"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));

        var resolution = BrickService.Add(project, "user.mateo.inventory", $"file:{packed.Path}");

        Assert.Equal("user.mateo.inventory", Assert.Single(resolution.Packages).Id);
        Assert.Single(BrickService.List(project));
        Assert.Throws<PackageException>(() => BrickService.Add(project, "user.mateo.missing", "file:../nowhere"));
        Assert.DoesNotContain("user.mateo.missing", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.True(BrickService.Remove(project, "user.mateo.inventory"));
    }
}
