namespace Turian.Tests;

/// <summary>How the studio plugin wires brick contributions into its start, shutdown and panels.</summary>
[Collection(SerialTests.Name)]
public sealed class StudioContributionWiringTests
{
    sealed class AnyService : IServiceProvider
    {
        // The panel only stores its services while it is built.
        public object GetService(Type serviceType)
        {
            if (serviceType.IsInterface)
            {
                return Substitute.For([serviceType], []);
            }

            var service = RuntimeHelpers.GetUninitializedObject(serviceType);
            GC.SuppressFinalize(service);
            return service;
        }
    }

    /// <summary>The plugin registers the bridge over its own registries, and shutdown saves open documents.</summary>
    [Fact]
    public void StoppingSavesDocumentsAndReleasesTheBridge()
    {
        using var project = new DocumentProject();
        var panels = new PanelRegistry();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IShellHost>());
        services.AddSingleton(Substitute.For<ICommandDispatcher>());
        services.AddSingleton(Substitute.For<IPanelAccessor>());
        services.AddSingleton(Substitute.For<IShortcutService>());
        services.AddSingleton(Substitute.For<IFocusTracker>());
        services.AddSingleton(Substitute.For<IEditorSettings>());
        services.AddSingleton<IDocumentService>(new DocumentService());
        var context = Substitute.For<IPluginContext>();
        context.Services.Returns(services);
        context.Panels.Returns(panels);
        context.Logger.Returns(NullLogger.Instance);
        context.CommandLineArgs.Returns([]);
        var plugin = new GayaPlugin();
        plugin.Configure(context);
        using var provider = services.BuildServiceProvider();
        plugin.Start(provider);
        var kind = new FakeDocumentKind();
        using var registration = provider.GetRequiredService<WorkspaceDocumentKindRegistry>().Add(kind);
        var workspace = provider.GetRequiredService<AssetWorkspace>();
        workspace.Open(project.Source("menu.fake").Path);
        var document = Assert.Single(kind.Opened);
        document.Edit();
        plugin.Tick(provider, 0.016f);

        plugin.Stop(provider);

        Assert.Equal([false], document.Saves);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.NotNull(provider.GetRequiredService<LivePluginBridge>());
    }

    /// <summary>Every Inspector instance is built with the inspector sources and documents.</summary>
    [Fact]
    public void InspectorInstancesAreBuiltFromTheStudioServices()
    {
        var panels = new PanelRegistry();
        var instances = new InspectorInstances(panels);
        instances.RegisterInitial(GayaPlugin.InspectorPanelId);
        var additional = instances.CreateAdditional();

        var panel = panels.All.Single(descriptor => descriptor.Id == additional).Factory(new AnyService());

        Assert.IsType<InspectorPanel>(panel);
        Assert.Equal(2, panels.All.Count);
    }

    /// <summary>The Build Profiles window is built with the studio's services, including the one that saves.</summary>
    [Fact]
    public void BuildProfilesIsBuiltFromTheStudioServices()
    {
        var descriptor = StudioContributionsTests.Configure().Panels
            .Single(static panel => panel.Id == GayaPlugin.BuildProfilesPanelId);

        Assert.IsType<BuildProfilesPanel>(descriptor.Factory(new AnyService()));
    }

    /// <summary>A disposed panel catalog stays empty however often it is refreshed.</summary>
    [Fact]
    public void ADisposedPanelCatalogStaysEmpty()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var catalog = new UserPanelCatalog(build, NullLogger.Instance);
        catalog.Dispose();

        catalog.EnsureRefreshed();

        Assert.Empty(catalog.Pages);
    }
}
