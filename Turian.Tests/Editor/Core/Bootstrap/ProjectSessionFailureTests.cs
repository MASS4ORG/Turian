namespace Turian.Tests;

/// <summary>Project compilation reports a missing brick as a failed task rather than a background exception.</summary>
[Collection(SerialTests.Name)]
public sealed class ProjectSessionFailureTests
{
    /// <summary>The original brick name and repair instructions survive both task layers without stack traces.</summary>
    [Fact]
    public async Task MissingBrickIsAnActionableFailedTask()
    {
        var root = Directory.CreateTempSubdirectory("turian-session-failure-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            new ProjectManifest
            {
                Dependencies =
                {
                    [ProjectPackages.RenderBrick] = null,
                    ["com.example.missing"] = "file:missing-brick",
                },
            }.Save(root);
            var logger = Substitute.For<ILogger>();
            using var build = new BuildManager(new AppSettings { ProjectAbsoluteDir = root }, logger);
            var tasks = new BackgroundTaskManager();
            using var services = new ServiceCollection()
                .AddSingleton(build)
                .AddSingleton(new BackgroundTaskRunner(tasks, logger))
                .BuildServiceProvider();
            var session = new ProjectSession(services, logger);

            var id = session.RecompileScripts();
            await BackgroundTaskRunnerTests.WaitForCompletion(tasks, id);

            var task = tasks.Get(id)!;
            Assert.Equal(BackgroundTaskStatus.Failed, task.Status);
            Assert.Contains("com.example.missing", task.Note);
            Assert.Contains("Fix the brick sources or registry trust, then retry", task.Note);
            var events = logger.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == "Log").ToArray();
            Assert.NotEmpty(events);
            Assert.All(events, static call => Assert.Null(call.GetArguments()[3]));
            Assert.DoesNotContain(events, static call => call.GetArguments()[2]!.ToString()!
                .Contains("Background task Compile scripts failed", StringComparison.Ordinal));
        }
        finally
        {
            ProjectPackages.Invalidate(root);
            Directory.Delete(root, true);
        }
    }
}
