namespace Gaya.Tests;

/// <summary>
/// The Bricks panel refreshes registries in the background while the user installs and removes bricks. Every
/// interleaving here is steered by gates inside a workspace double, never by sleeps or retries; the double also
/// records any two operations that ran at the same time, which is what lets a restore and an installation tear each
/// other's reads.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class BricksControllerRaceTests : IDisposable
{
    const string Sunset = "com.acme.sunset";

    static readonly Dictionary<string, SemanticVersion> Hosts = new() { ["gaya"] = SemanticVersion.Parse("1.0.0") };
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-bricks-race-{Guid.NewGuid():N}");

    /// <summary>Creates a studio folder and a local theme brick beside it.</summary>
    public BricksControllerRaceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "studio"));
        var sunset = Path.Combine(root, "sunset");
        Directory.CreateDirectory(Path.Combine(sunset, "Themes"));
        new PackageManifest
        {
            Name = Sunset,
            Version = SemanticVersion.Parse("1.0.0"),
            Scopes = [PackageScope.Studio],
            Categories = [ThemeCategories.ColorTheme],
        }.Save(sunset);
        File.WriteAllText(Path.Combine(sunset, "Themes", "sunset.pss"), """
            @const theme-id = "acme.sunset";
            @const theme-name = "Sunset";
            @import "gaya.base";
            $accent = #ff8800;
            """);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    string SunsetSource => $"file:{Path.Combine(root, "sunset")}";

    GatedWorkspace Studio(string name = "studio")
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        return new GatedWorkspace(new StudioBricks(Path.Combine(root, name), Hosts, new PackageStore(Path.Combine(root, "store"))));
    }

    static string[] Ids(BricksController controller) => [.. controller.Rows.Select(static row => row.Id)];

    static Task Within(Task task) => task.WaitAsync(Limit, TestContext.Current.CancellationToken);

    static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Limit, TestContext.Current.CancellationToken);

    /// <summary>
    /// A signal that fires once the request made while a restore is held has either finished (it ran beside the
    /// restore) or is known to be waiting for its turn.
    /// </summary>
    static Task SettledSignal(BricksController controller, Func<bool> finished)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () =>
        {
            if (finished() || controller.QueuedOperations > 0)
            {
                _ = signal.TrySetResult();
            }
        };
        return signal.Task;
    }

    /// <summary>An installation requested during a registry refresh waits for it, and is listed afterwards.</summary>
    [Fact]
    public async Task AnInstallationDuringARefreshWaitsForTheRestoreAndStaysListed()
    {
        var workspace = Studio();
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio };
        var settled = SettledSignal(controller, () => controller.Rows.Any(static row => row.Id == Sunset));

        workspace.HoldNextRestore();
        var refresh = controller.RefreshRegistriesAsync();
        await Within(workspace.RestoreHeld);

        var install = controller.AddFromSourceAsync(SunsetSource);
        await Within(settled);
        workspace.ReleaseRestore();

        Assert.True(await Within(install));
        await Within(refresh);
        Assert.Equal([Sunset], Ids(controller));
        Assert.Equal(0, workspace.Overlaps);
        Assert.False(controller.IsBusy);
        Assert.Equal(0, controller.QueuedOperations);
    }

    /// <summary>A removal requested during a registry refresh is not undone when the refresh completes.</summary>
    [Fact]
    public async Task ARemovalDuringARefreshWaitsForTheRestoreAndStaysRemoved()
    {
        var workspace = Studio();
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio };
        Assert.True(await Within(controller.AddFromSourceAsync(SunsetSource)));
        Assert.Equal([Sunset], Ids(controller));

        var settled = SettledSignal(controller, () => controller.Rows.Count == 0);
        workspace.HoldNextRestore();
        var refresh = controller.RefreshRegistriesAsync();
        await Within(workspace.RestoreHeld);

        var removal = controller.DisableAsync(Sunset);
        await Within(settled);
        workspace.ReleaseRestore();

        Assert.True(await Within(removal));
        await Within(refresh);
        Assert.Empty(controller.Rows);
        Assert.Equal(0, workspace.Overlaps);
    }

    /// <summary>A registry search still running when a brick is installed leaves the installed row alone.</summary>
    [Fact]
    public async Task ASearchCompletingAfterAnInstallationKeepsTheBrick()
    {
        var workspace = Studio();
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio };

        workspace.HoldSearch();
        var refresh = controller.RefreshRegistriesAsync();
        await Within(workspace.SearchHeld);

        Assert.True(await Within(controller.AddFromSourceAsync(SunsetSource)));
        Assert.Equal([Sunset], Ids(controller));

        workspace.ReleaseSearch();
        await Within(refresh);
        Assert.Equal([Sunset], Ids(controller));
        Assert.Equal(0, workspace.Overlaps);
        Assert.False(controller.IsBusy);
    }

    /// <summary>A refresh still searching when another workspace opens neither restores nor publishes into it.</summary>
    [Fact]
    public async Task ASearchCompletingAfterAWorkspaceSwitchPublishesNothing()
    {
        var first = Studio("first");
        var second = Studio("second");
        var project = new SwitchableProject { Workspace = first };
        var controller = new BricksController(null, project);
        Assert.True(controller.HasWorkspace);

        first.HoldSearch();
        var refresh = controller.RefreshRegistriesAsync();
        await Within(first.SearchHeld);

        project.Workspace = second;
        controller.Refresh();
        var revision = controller.Revision;
        first.ReleaseSearch();
        await Within(refresh);

        Assert.Equal(revision, controller.Revision);
        Assert.Equal(0, first.Restores);
        Assert.Equal(0, second.Restores);
        Assert.Empty(controller.Rows);
    }

    /// <summary>An action finishing after the workspace changed does not publish into the new one.</summary>
    [Fact]
    public async Task AnActionFinishingAfterAWorkspaceSwitchPublishesNothing()
    {
        var first = Studio("first");
        var second = Studio("second");
        var project = new SwitchableProject { Workspace = first };
        var controller = new BricksController(null, project);

        first.HoldNextRestore();
        var restore = controller.RestoreAsync();
        await Within(first.RestoreHeld);

        project.Workspace = second;
        controller.Refresh();
        var revision = controller.Revision;
        first.ReleaseRestore();

        Assert.True(await Within(restore));
        Assert.Equal(revision, controller.Revision);
        Assert.Empty(controller.Rows);
        Assert.False(controller.IsBusy);
    }

    /// <summary>A refresh superseded by a newer one by the time its search returns does nothing.</summary>
    [Fact]
    public async Task ASupersededRefreshDoesNotRestore()
    {
        var workspace = Studio();
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio };

        workspace.HoldSearch();
        var older = controller.RefreshRegistriesAsync();
        await Within(workspace.SearchHeld);
        var newer = controller.RefreshRegistriesAsync();
        workspace.ReleaseSearch();

        await Within(Task.WhenAll(older, newer));
        Assert.Equal(1, workspace.Restores);
    }

    /// <summary>Operations queued behind a refresh keep the panel busy until the last one ends, and keep their errors.</summary>
    [Fact]
    public async Task QueuedOperationsKeepTheBusyStateAndTheirErrors()
    {
        var workspace = Studio();
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio };
        var settled = SettledSignal(controller, static () => false);
        var busy = new List<bool>();
        controller.Changed += () => busy.Add(controller.IsBusy);

        workspace.HoldNextRestore();
        var refresh = controller.RefreshRegistriesAsync();
        await Within(workspace.RestoreHeld);
        var failure = controller.AddFromSourceAsync($"file:{Path.Combine(root, "missing")}");
        await Within(settled);
        Assert.True(controller.IsBusy);
        Assert.Equal(1, controller.QueuedOperations);
        workspace.ReleaseRestore();

        Assert.False(await Within(failure));
        await Within(refresh);
        Assert.False(controller.IsBusy);
        Assert.Equal(0, controller.QueuedOperations);
        Assert.NotNull(controller.Error);
        Assert.Contains("missing", controller.Error, StringComparison.Ordinal);
        Assert.Equal(0, workspace.Overlaps);
        Assert.DoesNotContain(false, busy.Skip(busy.IndexOf(true)).SkipLast(1));
    }

    /// <summary>A project whose workspace the test replaces.</summary>
    sealed class SwitchableProject : IProjectBricks
    {
        public IBrickWorkspace? Workspace { get; set; }
    }

    /// <summary>
    /// A studio workspace whose search and restore a test can hold and release, and which counts operations that
    /// overlapped in time.
    /// </summary>
    sealed class GatedWorkspace(StudioBricks inner) : IBrickWorkspace
    {
        readonly Lock sync = new();
        readonly TaskCompletionSource restoreGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource searchGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource restoreHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource searchHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdRestore;
        bool holdSearch;
        int running;
        int overlaps;
        int restores;

        public Task RestoreHeld => restoreHeld.Task;

        public Task SearchHeld => searchHeld.Task;

        public int Restores => Volatile.Read(ref restores);

        /// <summary>How many times an operation began while another was still running.</summary>
        public int Overlaps => Volatile.Read(ref overlaps);

        /// <summary>The next restore begins, then waits before it ends.</summary>
        public void HoldNextRestore()
        {
            lock (sync)
            {
                holdRestore = true;
            }
        }

        public void ReleaseRestore() => restoreGate.TrySetResult();

        /// <summary>The next search waits before it answers.</summary>
        public void HoldSearch()
        {
            lock (sync)
            {
                holdSearch = true;
            }
        }

        public void ReleaseSearch() => searchGate.TrySetResult();

        public async Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string query)
        {
            bool hold;
            lock (sync)
            {
                hold = holdSearch;
                holdSearch = false;
            }

            if (hold)
            {
                searchHeld.TrySetResult();
                await searchGate.Task.ConfigureAwait(false);
            }

            return [];
        }

        public void Restore()
        {
            _ = Interlocked.Increment(ref restores);
            bool hold;
            lock (sync)
            {
                hold = holdRestore;
                holdRestore = false;
            }

            Run(() =>
            {
                inner.Restore();
                if (hold)
                {
                    restoreHeld.TrySetResult();
                    restoreGate.Task.Wait(Limit, TestContext.Current.CancellationToken);
                }
            });
        }

        void Run(Action operation) => Run(() =>
        {
            operation();
            return 0;
        });

        T Run<T>(Func<T> operation)
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                _ = Interlocked.Increment(ref overlaps);
            }

            try
            {
                return operation();
            }
            finally
            {
                _ = Interlocked.Decrement(ref running);
            }
        }

        public IReadOnlyList<ResolvedPackage> Resolve() => Run(inner.Resolve);

        public void Add(string id, string? source) => Run(() => inner.Add(id, source));

        public string AddFromSource(string spec, PackageStore store) => Run(() => inner.AddFromSource(spec, store));

        public void Disable(string id) => Run(() => inner.Disable(id));

        public bool Remove(string id) => Run(() => inner.Remove(id));

        public void Update(IReadOnlyCollection<string> ids) => Run(() => inner.Update(ids));

        public void Embed(string id) => Run(() => inner.Embed(id));

        public void Revert(string id) => Run(() => inner.Revert(id));

        public void CopyAssets(string id, IReadOnlyCollection<string> assets, string destination) =>
            Run(() => inner.CopyAssets(id, assets, destination));

        public void AddRegistry(ScopedRegistry registry) => Run(() => inner.AddRegistry(registry));

        public bool RemoveRegistry(string name) => Run(() => inner.RemoveRegistry(name));

        public void SaveManifest(ProjectManifest manifest) => Run(() => inner.SaveManifest(manifest));

        public string Root => inner.Root;

        public PackageScope Scope => inner.Scope;

        public IReadOnlyCollection<string> ReservedCategoryPrefixes => inner.ReservedCategoryPrefixes;

        public string? BuiltinDirectory => inner.BuiltinDirectory;

        public ScopedRegistry? PublicRegistry => inner.PublicRegistry;

        public bool SupportsForks => inner.SupportsForks;

        public IReadOnlyList<ScopedRegistry> Registries() => inner.Registries();

        public bool IsPrecast(ResolvedPackage brick) => inner.IsPrecast(brick);

        public void Changed(bool applied) => inner.Changed(applied);
    }
}
