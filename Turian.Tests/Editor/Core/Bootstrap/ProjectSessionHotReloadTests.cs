namespace Turian.Tests;

/// <summary>Exercises the source-watch lifecycle used when a Studio project opens.</summary>
[Collection(SerialTests.Name)]
public sealed class ProjectSessionHotReloadTests
{
    /// <summary>Opening a project begins watching its Assets directory after the initial compile attempt.</summary>
    [Fact]
    public async Task OpeningProject_SchedulesRecompileForEditedUserScript()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-hot-reload-{Guid.NewGuid():N}");
        TestAssetDatabase.Reset();
        try
        {
            var project = await new ProjectBootstrapper().CreateAsync(root);
            Assert.NotNull(project);
            using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance).BuildServiceProvider();
            var session = new ProjectSession(services, NullLogger.Instance);
            Assert.True(session.Open(project));

            var build = services.GetRequiredService<BuildManager>();
            var completed = new TaskCompletionSource<BuildTaskStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            build.TaskRunner.TaskCompleted += status =>
            {
                if (status.TaskName == "Compile & Load") completed.TrySetResult(status);
            };

            var source = Path.Combine(project, "Assets", "Game.cs");
            await File.AppendAllTextAsync(source, "\n// Changed after opening the project.\n",
                TestContext.Current.CancellationToken);

            var status = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.Equal("Compile & Load", status.TaskName);
        }
        finally
        {
            TestAssetDatabase.Reset();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
