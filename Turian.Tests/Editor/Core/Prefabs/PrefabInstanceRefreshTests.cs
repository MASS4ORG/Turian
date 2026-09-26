namespace Turian.Tests;

/// <summary>Tests for updating an open scene's instances when their prefab is saved.</summary>
public class PrefabInstanceRefreshTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    string? Load(Guid id) => prefabs.GetValueOrDefault(id);

    Node Instantiate(Guid prefabId) =>
        Serializer.LoadData<Node>(PrefabInstances.Expand(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            Load))!;

    static Node Lamp(float intensity) => new()
    {
        Name = "Lamp",
        Children = { new Node { Name = "Bulb", Components = { new LightComponent { Intensity = intensity } } } },
    };

    static float Intensity(Node instance) => instance.Children[0].GetComponent<LightComponent>()!.Intensity;

    /// <summary>A prefab edit reaches every instance except where an instance overrides the edited value.</summary>
    [Fact]
    public void Rebuild_AppliesPrefabChangesAndKeepsOverrides()
    {
        var lamp = Lamp(1f);
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = Serializer.Serialize(lamp);
        var plain = Instantiate(prefabId);
        var tuned = Instantiate(prefabId);
        tuned.Children[0].GetComponent<LightComponent>()!.Intensity = 5f;
        var scene = new Node { Name = "Scene", Children = { plain, tuned } };

        var previous = prefabs[prefabId];
        lamp.Children[0].GetComponent<LightComponent>()!.Intensity = 2f;
        lamp.Children.Add(new Node { Name = "Shade" });
        prefabs[prefabId] = Serializer.Serialize(lamp);

        var rebuilt = PrefabInstanceRefresh.Rebuild(scene, prefabId, previous, Load)!;

        Assert.Equal(2f, Intensity(rebuilt.Children[0]));
        Assert.Equal(5f, Intensity(rebuilt.Children[1]));
        Assert.Equal("Shade", rebuilt.Children[0].Children[1].Name);
        Assert.Equal(plain.Id, rebuilt.Children[0].Id);
        Assert.Same(rebuilt, rebuilt.Children[0].Parent);
    }

    /// <summary>A scene with no instances is left alone.</summary>
    [Fact]
    public void Rebuild_SceneWithoutInstances_ReturnsNull()
    {
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = Serializer.Serialize(Lamp(1f));

        Assert.Null(PrefabInstanceRefresh.Rebuild(new Node { Name = "Scene" }, prefabId, prefabs[prefabId], Load));
    }
}
