namespace Turian.Tests;

/// <summary>Checks deferred startup and cleanup without creating a desktop window.</summary>
[Collection(SerialTests.Name)]
[Trait("Category", "E2E")]
public sealed class ProjectStartupTests
{
    /// <summary>Queueing a project performs no load and duplicate startup requests are rejected.</summary>
    [Fact]
    public void QueueDefersLoadingUntilTick()
    {
        var session = new ProjectSession(Substitute.For<IServiceProvider>(), NullLogger.Instance);
        session.QueueOpen("/missing/project");
        Assert.True(session.IsOpening);
        Assert.Null(session.Settings);
        Assert.Throws<InvalidOperationException>(() => session.QueueOpen("/another/project"));
        Assert.False(session.TickStartup());
        Assert.False(session.IsOpening);
        session.FinishStartup();
        Assert.Throws<ArgumentException>(() => session.QueueOpen(""));
    }

    /// <summary>The project opens after background import and shutdown waits for pending startup.</summary>
    [Fact]
    public async Task BackgroundStartupCompletesBeforeServicesAreDisposed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-startup-{Guid.NewGuid():N}");
        try
        {
            var project = await new ProjectBootstrapper().CreateAsync(root);
            Assert.NotNull(project);
            using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance).BuildServiceProvider();
            var session = new ProjectSession(services, NullLogger.Instance);
            session.QueueOpen(project);
            Assert.Null(session.Settings);
            Assert.True(session.TickStartup());
            Assert.True(session.IsOpening);
            session.FinishStartup();
            Assert.False(session.IsOpening);
            Assert.Equal(project, session.Settings!.ProjectAbsoluteDir);
            Assert.False(session.TickStartup());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Changing projects saves the outgoing document session and performs the new import through tracked work.</summary>
    [Fact]
    public async Task ProjectSwitchPreservesOutgoingSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-project-switch-{Guid.NewGuid():N}");
        try
        {
            var first = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "first"));
            var second = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "second"));
            Assert.NotNull(first);
            Assert.NotNull(second);
            using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance).BuildServiceProvider();
            var session = new ProjectSession(services, NullLogger.Instance);
            Assert.True(session.Open(first));
            var workspace = services.GetRequiredService<AssetWorkspace>();
            var prefab = services.GetRequiredService<AssetDatabase>().GetAssetsSnapshot()
                .Select(AssetReferenceQuery.CreateAsset).OfType<Prefab>().First();
            workspace.Open(prefab);
            session.QueueOpen(second);
            Assert.Equal(first, session.Settings!.ProjectAbsoluteDir);
            session.TickStartup();
            var tasks = services.GetRequiredService<BackgroundTaskManager>();
            Assert.Contains(tasks.Snapshot(), task => task.Label == "Opening project" && task.BlocksUi);
            session.FinishStartup();
            Assert.Equal(second, session.Settings.ProjectAbsoluteDir);
            Assert.False(session.IsOpening);
            var saved = services.GetRequiredService<WorkspaceSessionStore>().Load(first);
            Assert.Contains(prefab.Id, saved.OpenAssetIds);
            Assert.Contains(tasks.Snapshot(), task => task.Label == "Checking assets" && task.UnitsTotal > 0);
            Assert.Contains(tasks.Snapshot(), task => task.Label == "Indexing assets" && task.UnitsTotal > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
