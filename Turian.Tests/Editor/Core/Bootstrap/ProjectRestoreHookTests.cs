namespace Turian.Tests;

/// <summary>Contributions announced before a project's documents are restored can open them.</summary>
[Collection(SerialTests.Name)]
[Trait("Category", "E2E")]
public sealed class ProjectRestoreHookTests
{
    /// <summary>A kind added while the session is about to be restored opens the restored documents.</summary>
    [Fact]
    public async Task AKindAddedBeforeRestoringOpensTheRestoredDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-restore-hook-{Guid.NewGuid():N}");
        try
        {
            var project = await new ProjectBootstrapper().CreateAsync(root);
            Assert.NotNull(project);
            ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
            using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance).BuildServiceProvider();
            var session = new ProjectSession(services, NullLogger.Instance);
            Assert.True(session.Open(project));
            var workspace = services.GetRequiredService<AssetWorkspace>();
            var prefab = services.GetRequiredService<AssetDatabase>().GetAssetsSnapshot()
                .Select(AssetReferenceQuery.CreateAsset).OfType<Prefab>().First();
            workspace.Open(prefab);
            session.SaveSession();
            workspace.CloseAll();

            var kind = new FakeDocumentKind { Extension = ".prefab" };
            IDisposable? registration = null;
            var openBefore = -1;

            // A new session has nothing of its own to save over the recorded documents.
            var restarted = new ProjectSession(services, NullLogger.Instance);
            restarted.Restoring += () =>
            {
                openBefore = workspace.Documents.Count;
                registration = services.GetRequiredService<WorkspaceDocumentKindRegistry>().Add(kind);
            };
            Assert.True(restarted.Open(project));

            Assert.Equal(0, openBefore);
            Assert.Equal(prefab.Id.ToString(), Assert.Single(kind.Opened).Id);
            registration!.Dispose();
        }
        finally
        {
            ProjectPackages.Invalidate(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
