namespace Turian.Tests;

/// <summary>Verifies that plugin startup defers project loading for an interactive host.</summary>
[Collection(SerialTests.Name)]
[Trait("Category", "E2E")]
public sealed class StudioStartupLifecycleTests
{
    /// <summary>The plugin queues startup, ticks it to completion and shuts down its services cleanly.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractivePluginQueuesProjectBeforeLoading(bool missingBrick)
    {
        LogBuffer.Clear();
        var root = Path.Combine(Path.GetTempPath(), $"turian-plugin-startup-{Guid.NewGuid():N}");
        try
        {
            var project = await new ProjectBootstrapper().CreateAsync(root);
            Assert.NotNull(project);
            if (missingBrick)
            {
                new ProjectManifest
                {
                    Dependencies =
                    {
                        [ProjectPackages.RenderBrick] = null,
                        ["com.example.unpublished"] = "file:missing-brick",
                    },
                }.Save(project);
            }
            var logger = LogBuffer.Provider.CreateLogger("Editor");
            var services = new ServiceCollection();
            services.AddSingleton(logger);
            services.AddSingleton(Substitute.For<IShellHost>());
            services.AddSingleton(Substitute.For<ICommandDispatcher>());
            services.AddSingleton(Substitute.For<IPanelAccessor>());
            services.AddSingleton(Substitute.For<IShortcutService>());
            services.AddSingleton(Substitute.For<IFocusTracker>());
            services.AddSingleton(Substitute.For<IEditorSettings>());
            var context = Substitute.For<IPluginContext>();
            context.Services.Returns(services);
            context.Logger.Returns(logger);
            context.CommandLineArgs.Returns(["--project", project]);
            var plugin = new GayaPlugin();
            plugin.Configure(context);
            using var provider = services.BuildServiceProvider();
            plugin.Start(provider);
            var session = provider.GetRequiredService<ProjectSession>();
            Assert.True(session.IsOpening);
            Assert.Null(session.Settings);
            plugin.Tick(provider, 0.016f);
            Assert.NotNull(session.Settings);
            session.FinishStartup();
            plugin.Tick(provider, 0.016f);
            Assert.False(session.IsOpening);
            var startup = Assert.Single(provider.GetRequiredService<BackgroundTaskManager>().Snapshot(),
                static task => task.Label == "Opening project");
            Assert.Equal(missingBrick ? "Project opened with script errors; see Output" : "Project ready", startup.Note);
            if (missingBrick)
            {
                var lines = LogBuffer.Snapshot();
                Assert.Contains(lines, static line => line.Level == LogLevel.Error
                    && line.Text.Contains("com.example.unpublished"));
                Assert.DoesNotContain(lines, static line => line.Text.Contains("at Turian."));
            }
            plugin.Stop(provider);
        }
        finally
        {
            ProjectPackages.Invalidate(root);
            LogBuffer.Clear();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
