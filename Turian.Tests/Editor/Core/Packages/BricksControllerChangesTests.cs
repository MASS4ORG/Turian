namespace Turian.Tests;

/// <summary>Staged brick changes, version choices, old downloads and bricks of the other scope.</summary>
public sealed class BricksControllerChangesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bricks-changes-{Guid.NewGuid():N}");
    readonly string project;
    readonly IBrickApplier applier = Substitute.For<IBrickApplier>();
    readonly SettingsService settings = new();
    readonly StudioBricks studio;
    readonly BricksController controller;

    /// <summary>Opens a scaffolded project beside an empty studio.</summary>
    public BricksControllerChangesTests()
    {
        project = new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")).GetAwaiter().GetResult()!;
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        var store = new PackageStore(Path.Combine(root, "store"));
        studio = new StudioBricks(Path.Combine(root, "studio"), TurianStudioBricks.Hosts, store);
        Directory.CreateDirectory(studio.Root);
        controller = TestBricks.Controller(settings, new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            applier, studio);
        controller.Store = store;
        controller.Scope = BrickScope.Project;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    string Source(string id) => $"file:{BrickService.New(Path.Combine(root, "fixtures"), id)}";

    /// <summary>Several staged changes apply in one pass: every manifest edit lands and the project recompiles once.</summary>
    [Fact]
    public async Task StagedChangesApplyWithOneRecompile()
    {
        controller.Refresh();
        controller.Stage(new BrickChange(BrickChangeKind.Enable, "user.mateo.rules", BrickScope.Project, Source("user.mateo.rules")));
        controller.Stage(new BrickChange(BrickChangeKind.Enable, "user.mateo.shop", BrickScope.Project, Source("user.mateo.shop")));
        controller.Stage(new BrickChange(BrickChangeKind.Disable, ProjectPackages.RenderBrick, BrickScope.Project));
        applier.DidNotReceive().ApplyBrickChanges();

        Assert.True(await controller.ApplyPendingAsync(), controller.Error);

        var declared = ProjectManifest.Load(project).Manifest.Dependencies;
        Assert.Contains("user.mateo.rules", declared.Keys);
        Assert.Contains("user.mateo.shop", declared.Keys);
        Assert.Null(declared.GetValueOrDefault(ProjectPackages.RenderBrick));
        Assert.DoesNotContain(controller.Rows, static r => r.Id == ProjectPackages.RenderBrick);
        applier.Received(1).ApplyBrickChanges();
        Assert.Empty(controller.Pending);
    }

    /// <summary>A change that fails is reported while the others still apply, with one recompile.</summary>
    [Fact]
    public async Task AFailedChangeDoesNotStopTheOthers()
    {
        controller.Refresh();
        controller.Stage(new BrickChange(BrickChangeKind.Enable, "user.mateo.rules", BrickScope.Project, Source("user.mateo.rules")));
        controller.Stage(new BrickChange(BrickChangeKind.Enable, "user.mateo.missing", BrickScope.Project, "file:../nowhere"));

        Assert.False(await controller.ApplyPendingAsync());

        Assert.Contains("user.mateo.rules", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.DoesNotContain("user.mateo.missing", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.Contains("nowhere", controller.Error, StringComparison.Ordinal);
        applier.Received(1).ApplyBrickChanges();
    }

    /// <summary>An update replaces a declared range that excludes the newest version, keeping an exact pin exact.</summary>
    [Theory]
    [InlineData("1.0.0", "1.2.0")]
    [InlineData("~1.0.0", "^1.2.0")]
    [InlineData("^1.0.0", null)]
    public void UpdatesReplaceRangesThatExcludeTheNewestVersion(string declared, string? expected)
    {
        var manifest = ProjectManifest.Load(project, includeUserOverride: false).Manifest;
        manifest.Dependencies["user.mateo.remote"] = declared;
        manifest.Save(project);
        var brick = new CatalogBrick("user.mateo.remote", null, null, null, BrickState.Enabled, "1.0.0", "1.2.0",
            "registry:bricks.example", "^1.0.0", IsBuiltin: false);

        controller.StageUpdate(brick);

        var change = Assert.Single(controller.Pending);
        Assert.Equal(BrickChangeKind.Update, change.Kind);
        Assert.Equal(expected, change.Source);
    }

    /// <summary>Old copies of a brick can be removed from the store; the newest stays.</summary>
    [Fact]
    public async Task OldDownloadsAreRemovedButNotTheNewest()
    {
        Directory.CreateDirectory(controller.Store.Root);
        var old = Path.Combine(controller.Store.Root, "user.mateo.aside@a");
        Directory.Move(BrickService.New(Path.Combine(root, "v1"), "user.mateo.aside"), old);
        var newest = BrickService.New(Path.Combine(root, "v2"), "user.mateo.aside");
        new PackageManifest { Name = "user.mateo.aside", Version = SemanticVersion.Parse("0.2.0") }.Save(newest);
        var kept = Path.Combine(controller.Store.Root, "user.mateo.aside@b");
        Directory.Move(newest, kept);
        controller.Refresh();
        var brick = controller.Catalog.Single(static b => b.Id == "user.mateo.aside");

        Assert.Equal([old], controller.UnusedDownloads(brick).Select(static c => c.Folder));
        Assert.Equal([old], controller.StaleDownloads().Select(static c => c.Folder));
        Assert.True(await controller.RemoveUnusedDownloadsAsync(brick), controller.Error);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(kept));
        applier.DidNotReceive().ApplyBrickChanges();
    }

    /// <summary>
    /// A studio-only brick found while managing the project installs into the studio, and the panel moves there;
    /// the project is not recompiled for it.
    /// </summary>
    [Fact]
    public async Task StudioOnlyBricksInstallIntoTheStudio()
    {
        var folder = BrickService.New(Path.Combine(root, "fixtures"), "user.mateo.panel");
        var manifest = new PackageManifest { Name = "user.mateo.panel", Version = SemanticVersion.Parse("0.1.0"), Scopes = [PackageScope.Studio] };
        manifest.Save(folder);
        var brick = new CatalogBrick("user.mateo.panel", null, null, null, BrickState.Available, null, "0.1.0", null,
            $"file:{folder}", IsBuiltin: false)
        { Manifest = manifest };
        controller.Refresh();

        Assert.Equal(BrickScope.Studio, controller.ScopeFor(brick));
        controller.Toggle(brick);
        Assert.Equal(BrickScope.Studio, Assert.Single(controller.Pending).Scope);
        Assert.True(await controller.ApplyPendingAsync(), controller.Error);

        Assert.Contains("user.mateo.panel", ProjectManifest.Load(studio.Root).Manifest.Dependencies.Keys);
        Assert.DoesNotContain("user.mateo.panel", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.Equal(BrickScope.Studio, controller.Scope);
        applier.DidNotReceive().ApplyBrickChanges();
    }

    /// <summary>A studio brick the studio already uses shows as in use while managing the project, not as one to add again.</summary>
    [Fact]
    public async Task StudioBricksInUseShowAsInUseFromTheProject()
    {
        var folder = BrickService.New(Path.Combine(root, "fixtures"), "user.mateo.panel");
        new PackageManifest { Name = "user.mateo.panel", Version = SemanticVersion.Parse("0.1.0"), Scopes = [PackageScope.Studio] }
            .Save(folder);
        controller.Scope = BrickScope.Studio;
        Assert.True(await controller.AddFromSourceAsync($"file:{folder}"), controller.Error);
        Directory.CreateDirectory(controller.Store.Root);
        CopyDirectory(folder, Path.Combine(controller.Store.Root, "user.mateo.panel@a"));

        controller.Scope = BrickScope.Project;

        var brick = controller.Catalog.Single(static b => b.Id == "user.mateo.panel");
        Assert.Equal(BrickState.Enabled, brick.State);
        Assert.False(brick.Accepts(PackageScope.Project));
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}
