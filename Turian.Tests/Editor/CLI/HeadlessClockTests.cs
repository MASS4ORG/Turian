using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Checks the headless host's clock injection and rejection of unusable project catalogs.</summary>
[Collection(SerialTests.Name)]
public sealed class HeadlessClockTests : IDisposable
{
    [TypeId("f7b9d010-c7e7-4c22-b4d2-3b5a4881f41d")]
    sealed class ClockComponent : Component
    {
        /// <summary>The headless host's session clock.</summary>
        [InjectService, JsonIgnore]
        public SimulationClock? Clock { get; private set; }
    }

    readonly string directory = Directory.CreateTempSubdirectory("turian-headless-clock-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>Loaded scene components receive a clock configured from the headless project's settings.</summary>
    [Fact]
    public void HeadlessHost_InjectsConfiguredSessionClock()
    {
        var assets = Directory.CreateDirectory(Path.Combine(directory, "Assets")).FullName;
        var sceneFile = Path.Combine(assets, "scene.prefab");
        var root = new Node();
        root.AddComponent(new ClockComponent());
        Serializer.Save(sceneFile, root);
        var asset = new Prefab { RelativePath = sceneFile };
        var database = new AssetDatabase();
        Assert.True(database.RegisterAsset(asset));
        database.SaveCatalog(directory);
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory, Title = "Headless clock" };
        settings.Loaded.Use(new TimeSettings { FixedDeltaTime = 0.02, MaxTicksPerFrame = 2 });

        using var project = HeadlessProject.Open(settings, NullLogger.Instance, withGraphics: false);
        var loaded = project.LoadScene(asset.Id.ToString());
        var component = Assert.IsType<ClockComponent>(loaded.Components.Single());
        var clock = Assert.IsType<SimulationClock>(component.Clock);

        Assert.Null(project.Vulkan);
        Assert.Equal(0.02, clock.FixedDeltaTime);
        Assert.Equal(2, clock.MaxTicksPerFrame);
        project.SceneManager.AdoptScene(asset.Id, loaded);
        new SceneTicker(project.SceneManager, clock).Tick(0.1);
        Assert.Equal(2, clock.TickIndex);
        Assert.Equal(0.06, clock.AccumulatorSeconds, 12);
    }

    /// <summary>Missing and truncated catalogs block creation of a gameplay host with a misleading empty world.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeadlessHost_RejectsUnusableCatalog(bool truncated)
    {
        if (truncated)
        {
            Directory.CreateDirectory(Path.Combine(directory, ".Cache"));
            File.WriteAllText(Path.Combine(directory, ".Cache", "assetCatalog.json"), "");
        }
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory };

        Assert.Throws<InvalidOperationException>(() =>
            HeadlessProject.Open(settings, NullLogger.Instance, withGraphics: false));
    }
}
