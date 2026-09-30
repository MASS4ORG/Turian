namespace Turian.Tests;

/// <summary>The state and actions behind the Bricks panel.</summary>
public sealed class BricksControllerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bricks-controller-{Guid.NewGuid():N}");
    readonly string project;
    readonly IBrickApplier applier = Substitute.For<IBrickApplier>();
    readonly SettingsService settings = new();
    readonly BricksController controller;

    /// <summary>Opens a scaffolded project, which installs the default built-in bricks.</summary>
    public BricksControllerTests()
    {
        project = new ProjectBootstrapper().CreateAsync(Path.Combine(root, "game")).GetAwaiter().GetResult()!;
        settings.Set(new AppSettings { Title = "Game", ProjectAbsoluteDir = project });
        controller = new BricksController(settings, new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            applier, NullLogger.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>Refreshing lists what the project installs, with how each got there.</summary>
    [Fact]
    public void RefreshListsTheInstalledBricks()
    {
        controller.Refresh();

        Assert.Equal(ProjectPackages.DefaultBuiltins.Order(StringComparer.Ordinal), controller.Rows.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.All(controller.Rows, r =>
        {
            Assert.Equal(PackageOrigin.Builtin, r.Origin);
            Assert.True(r.IsDirect);
        });
        Assert.True(controller.Rows.Single(r => r.Id == "org.mass4.turian.ui").IsPrecast);
        Assert.Null(controller.Error);
    }

    /// <summary>A project that is not open has no bricks, and actions say so instead of failing.</summary>
    [Fact]
    public async Task WithoutAProjectNothingRuns()
    {
        var closed = new BricksController(new SettingsService(), new BackgroundTaskRunner(new BackgroundTaskManager(), NullLogger.Instance),
            applier, NullLogger.Instance);

        closed.Refresh();
        Assert.False(closed.HasProject);
        Assert.Empty(closed.Rows);
        Assert.False(await closed.InstallAsync("org.mass4.turian.ui", null));
        Assert.NotNull(closed.Error);
        applier.DidNotReceive().ApplyBrickChanges();
    }

    /// <summary>Removing then installing a brick edits the manifest, applies the change and refreshes the rows.</summary>
    [Fact]
    public async Task RemoveAndInstallApplyTheChange()
    {
        controller.Refresh();
        var changes = 0;
        controller.Changed += () => changes++;

        Assert.True(await controller.RemoveAsync("org.mass4.turian.cameras"));
        Assert.DoesNotContain(controller.Rows, r => r.Id == "org.mass4.turian.cameras");
        Assert.DoesNotContain("org.mass4.turian.cameras", ProjectManifest.Load(project).Manifest.Dependencies.Keys);

        Assert.True(await controller.InstallAsync("org.mass4.turian.cameras", null));
        Assert.Contains(controller.Rows, r => r.Id == "org.mass4.turian.cameras");

        applier.Received(2).ApplyBrickChanges();
        Assert.True(changes > 0);
        Assert.False(controller.IsBusy);
    }

    /// <summary>A brick that cannot be installed leaves the manifest as it was and reports why.</summary>
    [Fact]
    public async Task FailedInstallsAreReportedAndUndone()
    {
        controller.Refresh();

        Assert.False(await controller.InstallAsync("user.mateo.missing", "file:../nowhere"));

        Assert.NotNull(controller.Error);
        Assert.DoesNotContain("user.mateo.missing", ProjectManifest.Load(project).Manifest.Dependencies.Keys);
        Assert.Equal(2, controller.Rows.Count);
    }

    /// <summary>Details say what a brick needs and what requires it.</summary>
    [Fact]
    public async Task DependenciesShowWhyABrickIsInstalled()
    {
        var rules = BrickService.New(root, "user.mateo.rules");
        var shop = BrickService.New(root, "user.mateo.shop");
        var manifest = PackageManifest.Load(shop, ["turian"]);
        manifest.Dependencies["user.mateo.rules"] = $"file:{rules}";
        manifest.Save(shop);

        Assert.True(await controller.InstallAsync("user.mateo.shop", $"file:{shop}"));

        Assert.Equal(["user.mateo.shop"], controller.RequiredBy("user.mateo.rules"));
        Assert.False(controller.Rows.Single(r => r.Id == "user.mateo.rules").IsDirect);
        controller.Selected = "user.mateo.shop";
        Assert.Equal("user.mateo.shop", controller.SelectedBrick!.Id);
    }

    /// <summary>Embedding forks the brick into the project.</summary>
    [Fact]
    public async Task EmbedForksTheBrick()
    {
        Assert.True(await controller.EmbedAsync("org.mass4.turian.cameras"));

        Assert.True(Directory.Exists(Path.Combine(project, "Packages", "org.mass4.turian.cameras")));
        Assert.Equal(PackageOrigin.Embedded, controller.Rows.Single(r => r.Id == "org.mass4.turian.cameras").Origin);
    }

    /// <summary>Reverting a fork returns the brick to its global version and keeps the fork's files in the trash.</summary>
    [Fact]
    public async Task RevertGoesBackToTheGlobalBrick()
    {
        Assert.True(await controller.EmbedAsync("org.mass4.turian.cameras"));
        var fork = Path.Combine(project, "Packages", "org.mass4.turian.cameras");
        File.WriteAllText(Path.Combine(fork, "mine.txt"), "changed");

        Assert.True(await controller.RevertAsync("org.mass4.turian.cameras"));

        Assert.False(Directory.Exists(fork));
        Assert.NotEqual(PackageOrigin.Embedded, controller.Rows.Single(r => r.Id == "org.mass4.turian.cameras").Origin);
        Assert.Single(Directory.GetFiles(Path.Combine(project, ".Cache", "Trash"), "mine.txt", SearchOption.AllDirectories));
    }

    /// <summary>A brick made in the project has no global version, so it cannot be reverted.</summary>
    [Fact]
    public async Task RevertRefusesABrickMadeInTheProject()
    {
        var made = BrickService.New(Path.Combine(project, "Packages"), "user.mateo.rules");
        controller.Refresh();

        Assert.False(await controller.RevertAsync("user.mateo.rules"));

        Assert.True(Directory.Exists(made));
    }

    /// <summary>An embedded-only brick is removed without losing the author's source files.</summary>
    [Fact]
    public async Task RemoveEmbeddedOnlyBrickPreservesSourcesInTrash()
    {
        var embedded = BrickService.New(Path.Combine(project, "Packages"), "user.mateo.rules");
        File.WriteAllText(Path.Combine(embedded, "authored.txt"), "rules");
        controller.Refresh();

        Assert.True(await controller.RemoveAsync("user.mateo.rules"));

        Assert.False(Directory.Exists(embedded));
        Assert.DoesNotContain(controller.Rows, row => row.Id == "user.mateo.rules");
        var preserved = Directory.GetFiles(Path.Combine(project, ".Cache", "Trash"), "authored.txt",
            SearchOption.AllDirectories);
        Assert.Equal("rules", File.ReadAllText(Assert.Single(preserved)));
    }

    /// <summary>Removing an embedded brick that supplies another brick fails without changing its files.</summary>
    [Fact]
    public async Task RequiredEmbeddedBrickCannotBeRemoved()
    {
        var packages = Path.Combine(project, "Packages");
        var rules = BrickService.New(packages, "user.mateo.rules");
        var shop = BrickService.New(packages, "user.mateo.shop");
        var manifest = PackageManifest.Load(shop, ["turian"]);
        manifest.Dependencies["user.mateo.rules"] = "^0.1.0";
        manifest.Save(shop);
        controller.Refresh();

        Assert.False(await controller.RemoveAsync("user.mateo.rules"));

        Assert.True(Directory.Exists(rules));
        Assert.Contains(controller.Rows, row => row.Id == "user.mateo.rules");
    }
}
