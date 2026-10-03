using Turian.Engine.Hzb;

namespace Turian.Tests;

/// <summary>Checks installation, factory discovery and project opt-in for the optional visibility Brick.</summary>
[Collection(SerialTests.Name)]
public sealed class HzbBrickTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-hzb-brick-{Guid.NewGuid():N}");

    /// <summary>Creates a project with an empty settings folder.</summary>
    public HzbBrickTests() => Directory.CreateDirectory(Path.Combine(project, "Assets", "Settings"));

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(project, true);

    /// <summary>Installing the Brick provides its precast library and generated runtime type manifest entry.</summary>
    [Fact]
    public void BrickShipsAndRegistersItsFactory()
    {
        const string id = "org.mass4.turian.hzb";
        new ProjectManifest { Dependencies = { [id] = $"builtin:{id}" } }.Save(project);
        var brick = ProjectPackages.Resolve(project).Packages.Single(p => p.Id == id);
        Assert.Equal(["Turian.Engine.Hzb"],
            BrickAssemblies.RuntimeAssemblies(brick).Select(Path.GetFileNameWithoutExtension));
        BrickAssemblies.Load([brick], NullLogger.Instance);
        Assert.IsType<HzbCullingFactory>(OcclusionCullers.Find());
        Assert.Contains("Turian.Engine.Hzb",
            UserCodeTypeManifestGenerator.Generate(Path.Combine(project, "Assets"), NullLogger.Instance).PrecastAssemblies);
        Assert.Empty(BrickVerifier.Verify(brick.RootPath));
    }

    /// <summary>Only a project with an enabled settings payload automatically activates the visibility pass.</summary>
    [Fact]
    public void SettingsOptInIsPerProject()
    {
        TypeRegistry.ScanAssembly(typeof(HzbSettings).Assembly);
        var factory = new HzbCullingFactory();
        var database = new AssetDatabase();
        Assert.False(factory.IsEnabled(database));
        var path = Path.Combine(project, "Assets", "Settings", "HzbSettings.dataasset");
        var settings = new HzbSettings { Id = Guid.NewGuid() };
        var metadata = new DataAssetAsset { Id = settings.Id, RelativePath = path };
        File.WriteAllText(path, Serializer.Serialize(settings));
        Assert.True(database.RegisterAsset(metadata, path));
        Assert.False(factory.IsEnabled(database));
        settings.Enabled = true;
        File.WriteAllText(path, Serializer.Serialize(settings));
        Assert.True(factory.IsEnabled(database));
        Assert.False(factory.IsEnabled(new AssetDatabase()));
    }
}
