namespace Turian.Tests;

/// <summary>Checks migration warnings and project settings discovery through the existing DataAsset workflow.</summary>
[Collection(SerialTests.Name)]
public sealed class LayerMigrationTests
{
    /// <summary>Repeated orphan indices warn once per project filter and layer space, including child nodes.</summary>
    [Fact]
    public void MigrationWarnsOncePerIndexAndSpace()
    {
        var factoryField = typeof(Log).GetField("_factory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (ILoggerFactory)factoryField.GetValue(null)!;
        var factory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        Log.Configure(factory);
        try
        {
            var project = new AppSettings();
            project.Get<TagsAndLayersSettings>().PhysicsLayers.RemoveAll(slot => slot.Index >= 10);
            project.Get<TagsAndLayersSettings>().RenderLayers.RemoveAll(slot => slot.Index >= 10);
            using var services = new ServiceCollection().AddSingleton<IAppSettings>(project)
                .AddSingleton<LayerFilter>().BuildServiceProvider();
            var node = new Node
            {
                PhysicsLayer = 17,
                RenderLayer = 17,
                Children = [new Node { PhysicsLayer = 17, RenderLayer = 17 }],
            };
            node.Awake(null, services);
            var filter = services.GetRequiredService<LayerFilter>();
            filter.ResolvePhysics(17);
            filter.ResolveRender(17);
            var messages = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log")
                .Select(call => call.GetArguments()[2]!.ToString()).ToArray();
            Assert.Equal(2, messages.Length);
            Assert.Contains(messages, message => message!.Contains("Physics layer index 17", StringComparison.Ordinal));
            Assert.Contains(messages, message => message!.Contains("Render layer index 17", StringComparison.Ordinal));
            Assert.All(node.Children, child => Assert.Equal(0, child.PhysicsLayer));
        }
        finally { Log.Configure(previous); }
    }

    /// <summary>Source settings and packed settings share the same type, identities and configured names.</summary>
    [Fact]
    public void SettingsLoadThroughSourcesAndAssetLoader()
    {
        var directory = Directory.CreateTempSubdirectory("turian-layer-settings-");
        try
        {
            var project = new AppSettings { ProjectAbsoluteDir = directory.FullName };
            var settings = new TagsAndLayersSettings();
            settings.PhysicsLayers[7].Name = "Actors";
            settings.Tags.Add("Player");
            var path = ProjectSettingsFiles.Create(project, settings);
            ProjectSettingsLoader.LoadFromSources(project);
            Assert.Equal("Actors", project.Get<TagsAndLayersSettings>().FindPhysicsLayer(7)!.Name);
            Assert.Equal(settings.FindPhysicsLayer(7)!.Id, project.Get<TagsAndLayersSettings>().FindPhysicsLayer(7)!.Id);
            var id = Asset.Load(path + ".meta")!.Id;
            ProjectSettingsLoader.WriteIndex(project.AssetsAbsoluteDir,
                Path.Combine(directory.FullName, ProjectSettingsLoader.IndexFileName));
            var loader = Substitute.For<IAssetLoader>();
            loader.LoadContentAsync<ProjectSettingsAsset>(id).Returns(settings);
            ProjectSettingsLoader.Load(project, loader);
            Assert.Same(settings, project.Get<TagsAndLayersSettings>());
            var opened = new SettingsService();
            var filter = new LayerFilter(new AppSettings(), opened);
            opened.Set(project);
            Assert.Same(settings, filter.Settings);
            var node = new Node { PhysicsLayer = 7, RenderLayer = 9 };
            Assert.True(filter.IncludesPhysics(node, LayerMask.FromLayer(7)));
            Assert.False(filter.IncludesPhysics(node, LayerMask.FromLayer(9)));
            Assert.True(filter.IncludesRender(node, LayerMask.FromLayer(9)));
            Assert.False(filter.IncludesRender(node, LayerMask.FromLayer(7)));
        }
        finally { directory.Delete(recursive: true); }
    }
}
