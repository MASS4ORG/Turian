using System.Text.Json.Nodes;

namespace Turian.Tests;

/// <summary>Tests for saving prefab instances as differences and rebuilding them from the prefab on load.</summary>
public class PrefabInstancesTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    string? Load(Guid id) => prefabs.GetValueOrDefault(id);

    Guid AddPrefab(Node root)
    {
        var id = Guid.NewGuid();
        prefabs[id] = Serializer.Serialize(root);
        return id;
    }

    static Node Lamp(float intensity = 1f) => new()
    {
        Name = "Lamp",
        Children =
        {
            new Node
            {
                Name = "Bulb",
                Components = { new LightComponent { Intensity = intensity }, new MeshComponent() },
            },
        },
    };

    Node Instantiate(Guid prefabId) =>
        Serializer.LoadData<Node>(PrefabInstances.Expand(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            Load))!;

    string Save(Node scene) => PrefabInstances.Compact(Serializer.Serialize(scene), Load);

    Node Reload(string saved) => Serializer.LoadData<Node>(PrefabInstances.Expand(saved, Load))!;

    static Node Scene(params Node[] children)
    {
        var scene = new Node { Name = "Scene" };
        foreach (var child in children) scene.Children.Add(child);
        return scene;
    }

    /// <summary>Each instance gets the prefab's content under ids unique to it, and its root keeps the link.</summary>
    [Fact]
    public void Instantiate_DerivesUniqueIds()
    {
        var prefab = Lamp();
        var prefabId = AddPrefab(prefab);

        var first = Instantiate(prefabId);
        var second = Instantiate(prefabId);

        Assert.Equal("Bulb", first.Children[0].Name);
        Assert.NotEqual(first.Children[0].Id, second.Children[0].Id);
        Assert.NotEqual(prefab.Children[0].Id, first.Children[0].Id);
        Assert.Equal(PrefabInstances.DeriveId(first.Id, prefab.Children[0].Id), first.Children[0].Id);
        Assert.Equal(prefabId, first.PrefabInstance?.Source.AssetId);
    }

    /// <summary>An unmodified instance saves as its link alone, and a node that is no instance writes no link.</summary>
    [Fact]
    public void Compact_UnmodifiedInstanceSavesOnlyTheLink()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        instance.Transform.Position = new Vector3(3, 0, 0);

        var saved = JsonNode.Parse(Save(Scene(instance)))!;
        var savedInstance = saved["Children"]![0]!.AsObject();

        Assert.False(savedInstance.ContainsKey("Children"));
        Assert.False(savedInstance.ContainsKey("Components"));
        Assert.Null(savedInstance["PrefabInstance"]!["Overrides"]);
        Assert.False(saved.AsObject().ContainsKey("PrefabInstance"));
    }

    /// <summary>Overrides, additions and removals survive a save and reload, and keep their ids.</summary>
    [Fact]
    public void RoundTrip_KeepsInstanceChanges()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var bulb = instance.Children[0];
        bulb.GetComponent<LightComponent>()!.Intensity = 5f;
        bulb.Components.Remove(bulb.GetComponent<MeshComponent>()!);
        instance.Children.Add(new Node { Name = "Shade" });

        var saved = Save(Scene(instance));
        var reloaded = Reload(saved).Children[0];

        Assert.Equal(instance.Id, reloaded.Id);
        Assert.Equal(bulb.Id, reloaded.Children[0].Id);
        Assert.Equal(5f, reloaded.Children[0].GetComponent<LightComponent>()!.Intensity);
        Assert.Null(reloaded.Children[0].GetComponent<MeshComponent>());
        Assert.Equal(new[] { "Bulb", "Shade" }, reloaded.Children.Select(child => child.Name));
    }

    /// <summary>A prefab edit reaches saved instances, except where an instance overrides the edited value.</summary>
    [Fact]
    public void Reload_PicksUpPrefabChanges()
    {
        var prefab = Lamp();
        var prefabId = AddPrefab(prefab);
        var plain = Instantiate(prefabId);
        var overriding = Instantiate(prefabId);
        overriding.Children[0].GetComponent<LightComponent>()!.Intensity = 5f;
        var saved = Save(Scene(plain, overriding));

        prefab.Children[0].GetComponent<LightComponent>()!.Intensity = 2f;
        prefab.Children[0].GetComponent<LightComponent>()!.Color = new Vector4(1, 0, 0, 1);
        prefabs[prefabId] = Serializer.Serialize(prefab);
        var reloaded = Reload(saved);

        var plainLight = reloaded.Children[0].Children[0].GetComponent<LightComponent>()!;
        var overridingLight = reloaded.Children[1].Children[0].GetComponent<LightComponent>()!;
        Assert.Equal(2f, plainLight.Intensity);
        Assert.Equal(5f, overridingLight.Intensity);
        Assert.Equal(new Vector4(1, 0, 0, 1), overridingLight.Color);
    }

    /// <summary>A prefab nesting another prefab expands both, and an override deep inside the nested one is kept.</summary>
    [Fact]
    public void Nested_OverrideInsideNestedPrefabRoundTrips()
    {
        var lampId = AddPrefab(Lamp());
        var room = new Node { Name = "Room" };
        room.Children.Add(Instantiate(lampId));
        var roomId = Guid.NewGuid();
        prefabs[roomId] = Save(room);

        var instance = Instantiate(roomId);
        var light = instance.Children[0].Children[0].GetComponent<LightComponent>()!;
        light.Intensity = 7f;

        var reloaded = Reload(Save(Scene(instance))).Children[0];

        Assert.Equal("Lamp", reloaded.Children[0].Name);
        Assert.Equal(light.Id, reloaded.Children[0].Children[0].GetComponent<LightComponent>()!.Id);
        Assert.Equal(7f, reloaded.Children[0].Children[0].GetComponent<LightComponent>()!.Intensity);
    }

    /// <summary>An instance whose prefab is gone keeps its saved data instead of failing the load.</summary>
    [Fact]
    public void MissingPrefab_KeepsSavedInstance()
    {
        var prefabId = AddPrefab(Lamp());
        var instance = Instantiate(prefabId);
        instance.Children[0].GetComponent<LightComponent>()!.Intensity = 5f;
        var saved = Save(Scene(instance));
        var prefabJson = prefabs[prefabId];
        prefabs.Remove(prefabId);

        var reloaded = Reload(saved).Children[0];

        Assert.Empty(reloaded.Children);
        Assert.Single(reloaded.PrefabInstance!.Overrides);

        var resaved = Save(Reload(saved));
        prefabs[prefabId] = prefabJson;
        var restored = Reload(resaved).Children[0];
        Assert.Equal(5f, restored.Children[0].GetComponent<LightComponent>()!.Intensity);
    }

    /// <summary>A prefab that contains an instance of itself stops expanding instead of recursing forever.</summary>
    [Fact]
    public void SelfContainingPrefab_DoesNotRecurse()
    {
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = new JsonObject
        {
            ["__TypeId"] = TypeRegistry.GetIdOrThrow(typeof(Node)).ToString(),
            ["Name"] = "Loop",
            ["Id"] = Guid.NewGuid().ToString(),
            ["Children"] = new JsonArray(JsonNode.Parse(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()))),
        }.ToJsonString();

        var instance = Instantiate(prefabId);

        Assert.Equal("Loop", instance.Name);
        Assert.Empty(instance.Children[0].Children);
    }
}
