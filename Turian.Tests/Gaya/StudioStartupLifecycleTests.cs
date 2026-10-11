namespace Turian.Tests;

/// <summary>Verifies that plugin startup defers project loading for an interactive host.</summary>
[Collection(SerialTests.Name)]
[Trait("Category", "E2E")]
public sealed class StudioStartupLifecycleTests
{
    /// <summary>The plugin queues startup, ticks it to completion and shuts down its services cleanly.</summary>
    [Fact]
    public async Task InteractivePluginQueuesProjectBeforeLoading()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-plugin-startup-{Guid.NewGuid():N}");
        try
        {
            var project = await new ProjectBootstrapper().CreateAsync(root);
            Assert.NotNull(project);
            var services = new ServiceCollection();
            services.AddSingleton<ILogger>(NullLogger.Instance);
            services.AddSingleton(Substitute.For<IShellHost>());
            services.AddSingleton(Substitute.For<ICommandDispatcher>());
            services.AddSingleton(Substitute.For<IPanelAccessor>());
            services.AddSingleton(Substitute.For<IShortcutService>());
            services.AddSingleton(Substitute.For<IFocusTracker>());
            services.AddSingleton(Substitute.For<IEditorSettings>());
            var context = Substitute.For<IPluginContext>();
            context.Services.Returns(services);
            context.Logger.Returns(NullLogger.Instance);
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
            plugin.Stop(provider);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
