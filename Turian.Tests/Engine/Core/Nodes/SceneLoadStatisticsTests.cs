namespace Turian.Tests;

/// <summary>Checks that scene load timings belong to successfully loaded roots.</summary>
public sealed class SceneLoadStatisticsTests
{
    /// <summary>File and catalog loads report durations, while an unrelated root has no measurement.</summary>
    [Fact]
    public async Task SuccessfulLoadsKeepTheirOwnDurations()
    {
        var directory = Directory.CreateTempSubdirectory("turian-scene-timing-");
        try
        {
            var path = Path.Combine(directory.FullName, "Assets", "Scene.prefab");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serializer.Serialize(new Node { Name = "Measured scene" }));
            var database = new AssetDatabase();
            var prefab = new Prefab { Id = Guid.NewGuid(), RelativePath = path };
            Assert.True(database.RegisterAsset(prefab, path));
            var manager = new SceneManager(database);
            Assert.Null(manager.GetLoadMilliseconds(new Node()));
            var fileRoot = await manager.LoadNodeAsync(path);
            var catalogRoot = await manager.LoadNodeAsync(prefab.Id);
            Assert.True(manager.GetLoadMilliseconds(fileRoot) > 0);
            Assert.True(manager.GetLoadMilliseconds(catalogRoot) > 0);
            Assert.NotSame(fileRoot, catalogRoot);
            var tree = new SceneTreeController(new AssetManager(), new SettingsService(), null!,
                Substitute.For<IAssetLoader>(), manager, database);
            tree.OpenAsset(prefab);
            Assert.True(tree.CurrentSceneLoadMilliseconds > 0);
            File.WriteAllText(path, "invalid scene json");
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadNodeAsync(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadNodeAsync(prefab.Id));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
