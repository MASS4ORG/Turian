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
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    static bool IsDeclared(ResolvedPackage package) => !ProjectPackages.DefaultDependencies.ContainsKey(package.Id);

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

    /// <summary>Changing a personal link or removing a linked manifest invalidates the editor's cached resolution.</summary>
    [Fact]
    public void ProjectLinkChangesInvalidateTheCache()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        var first = BrickService.New(Path.Combine(root, "first"), "user.mateo.rules");
        var second = BrickService.New(Path.Combine(root, "second"), "user.mateo.rules");
        new ProjectManifest
        {
            Dependencies = { [ProjectPackages.RenderBrick] = null },
            Links = { ["user.mateo.rules"] = first },
        }.Save(project);
        Assert.Equal(first, Assert.Single(ProjectPackages.Resolve(project).Packages).RootPath);
        var lockPath = Path.Combine(project, "Bricks", LockFile.FileName);
        var lockBefore = File.ReadAllText(lockPath);
        File.WriteAllText(Path.Combine(project, "Bricks", ProjectManifest.LinksFileName),
            JsonSerializer.Serialize(new { links = new Dictionary<string, string> { ["user.mateo.rules"] = second } }));
        Assert.Equal(second, Assert.Single(ProjectPackages.Resolve(project).Packages).RootPath);
        Assert.Equal(lockBefore, File.ReadAllText(lockPath));
        File.Delete(Path.Combine(second, PackageManifest.FileName));
        Assert.Throws<PackageException>(() => ProjectPackages.Resolve(project));
    }

    /// <summary>
    /// A failed resolution is kept, so the editor's many callers neither resolve again nor log it again, until an
    /// explicit restore retries it.
    /// </summary>
    [Fact]
    public void FailedResolutionsAreKeptUntilARestore()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        var rules = Path.Combine(root, "user.mateo.rules");
        new ProjectManifest
        {
            Dependencies = { [ProjectPackages.RenderBrick] = null, ["user.mateo.rules"] = $"file:{rules}" },
        }.Save(project);

        var first = Assert.Throws<PackageException>(() => ProjectPackages.Resolve(project));
        Assert.Same(first, Assert.Throws<PackageException>(() => ProjectPackages.Resolve(project)));

        _ = BrickService.New(root, "user.mateo.rules");
        Assert.Same(first, Assert.Throws<PackageException>(() => ProjectPackages.Resolve(project)));
        Assert.Equal("user.mateo.rules", Assert.Single(BrickService.Restore(project).Packages).Id);
    }

    /// <summary>Both project and studio brick verification reject dependencies on a producer's local paths.</summary>
    [Theory]
    [InlineData(PackageScope.Project)]
    [InlineData(PackageScope.Studio)]
    public void VerificationRejectsLocalDependencies(PackageScope scope)
    {
        var folder = BrickService.New(root, "user.mateo.inventory");
        var manifest = PackageManifest.Load(folder, ["turian"]);
        manifest.Scopes = [scope];
        manifest.Dependencies["user.mateo.rules"] = "file:../rules";
        manifest.Save(folder);
        Assert.Contains(BrickVerifier.Verify(folder), static issue => issue.Contains("local path", StringComparison.Ordinal));
        Assert.Throws<PackageException>(() => BrickService.Pack(folder, Path.Combine(root, "out")));
    }

    /// <summary>An invalid manifest is reported by verification without scanning its assets.</summary>
    [Fact]
    public void VerificationReportsMalformedManifests()
    {
        File.WriteAllText(Path.Combine(root, PackageManifest.FileName), "{");
        Assert.Single(BrickVerifier.Verify(root));
    }

    /// <summary>A new studio-only content brick packs without asset metas, since the studio never imports its files.</summary>
    [Theory]
    [InlineData(ContentBrickKind.Look)]
    [InlineData(ContentBrickKind.Theme)]
    [InlineData(ContentBrickKind.IconTheme)]
    [InlineData(ContentBrickKind.FontPack)]
    public void NewContentBricksPack(ContentBrickKind kind)
    {
        var folder = ContentBricks.New(root, $"user.mateo.{kind.ToString().ToLowerInvariant()}", kind, ProjectPackages.GayaVersion);

        Assert.Empty(BrickVerifier.Verify(folder));
        Assert.True(File.Exists(BrickService.Pack(folder, Path.Combine(root, "out")).Path));
    }

    /// <summary>Every built-in brick passes the checks publishing runs, so each can be packed and moved to its own repository.</summary>
    [Fact]
    public void BuiltinBricksAreSound()
    {
        var bricks = Directory.EnumerateDirectories(ProjectPackages.BuiltinDirectory)
            .Where(static folder => File.Exists(Path.Combine(folder, PackageManifest.FileName))).ToList();

        Assert.NotEmpty(bricks);
        Assert.All(bricks, static folder => Assert.Empty(BrickVerifier.Verify(folder)));
    }

    /// <summary>A folder that declares no bricks is not given a Bricks folder with an empty lock file.</summary>
    [Fact]
    public void ResolvingAFolderWithoutBricksWritesNothing()
    {
        var folder = Path.Combine(root, "plain");
        Directory.CreateDirectory(folder);

        var resolution = ProjectPackages.Resolve(folder);

        Assert.Equal(ProjectPackages.DefaultDependencies.Keys, resolution.Packages.Select(static p => p.Id));
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
        Directory.CreateDirectory(Path.Combine(folder, ".github", "workflows"));
        File.WriteAllText(Path.Combine(folder, ".github", "workflows", "brick.yml"), "on: push");
        File.WriteAllText(Path.Combine(folder, ".gitlab-ci.yml"), "brick: {}");

        var issues = BrickVerifier.Verify(folder);

        Assert.Contains(issues, static i => i.Contains("NoMeta.txt has no .meta", StringComparison.Ordinal));
        Assert.Contains(issues, static i => i.Contains("Orphan.png.meta has no asset", StringComparison.Ordinal));
        Assert.Contains(issues, static i => i.Contains("Orphan.png.meta has no valid Id", StringComparison.Ordinal));
        Assert.Contains(issues, static i => i.Contains("reuses the id", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, static i => i.Contains("Free.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, static i => i.Contains("brick.yml", StringComparison.Ordinal) || i.Contains(".gitlab-ci", StringComparison.Ordinal));
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

        Assert.Equal("user.mateo.inventory", Assert.Single(resolution.Packages, IsDeclared).Id);
        Assert.Single(BrickService.List(project), IsDeclared);
        Assert.Throws<PackageException>(() => BrickService.Add(project, "user.mateo.missing", "file:../nowhere"));
        Assert.DoesNotContain("user.mateo.missing", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.True(BrickService.Remove(project, "user.mateo.inventory"));
    }
}
