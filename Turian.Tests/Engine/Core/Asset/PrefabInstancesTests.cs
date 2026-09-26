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

    /// <summary>A prefab node moved to another parent is saved in its new place with its id.</summary>
    [Fact]
    public void MovedPrefabNode_KeepsNewParentAndId()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var bulb = instance.Children[0];
        var shade = new Node { Name = "Shade" };
        instance.Children.Remove(bulb);
        instance.Children.Add(shade);
        shade.Children.Add(bulb);

        var reloaded = Reload(Save(Scene(instance))).Children[0];

        var movedBulb = Assert.Single(Assert.Single(reloaded.Children).Children);
        Assert.Equal(bulb.Id, movedBulb.Id);
        Assert.NotNull(movedBulb.GetComponent<LightComponent>());
    }

    /// <summary>An instance may hold another instance of its own prefab as an addition.</summary>
    [Fact]
    public void AddedInstanceOfSamePrefab_Expands()
    {
        var prefabId = AddPrefab(Lamp());
        var outer = Instantiate(prefabId);
        outer.Children.Add(Instantiate(prefabId));

        var reloaded = Reload(Save(Scene(outer))).Children[0];

        Assert.Equal("Bulb", reloaded.Children[1].Children[0].Name);
    }

    PrefabInstanceDiff Diff(Node scene) => PrefabInstances.Diff(Serializer.Serialize(scene), Load);

    /// <summary>An instance left as the prefab made it changes nothing.</summary>
    [Fact]
    public void Diff_UnmodifiedInstanceIsEmpty()
    {
        var diff = Diff(Scene(Instantiate(AddPrefab(Lamp()))));

        Assert.Empty(diff.Overrides);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.MissingPrefabs);
    }

    /// <summary>A changed member is reported against the object's id in the scene, not in the prefab.</summary>
    [Fact]
    public void Diff_ReportsOverriddenMember()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var light = instance.Children[0].GetComponent<LightComponent>()!;
        light.Intensity = 3f;

        var diff = Diff(Scene(instance));

        Assert.Equal([(light.Id, nameof(LightComponent.Intensity))], diff.Overrides);
        Assert.True(diff.HasOverrides(light.Id));
        Assert.False(diff.HasOverrides(instance.Children[0].Id));
    }

    /// <summary>The root's name and placement belong to the instance, so they are not overrides.</summary>
    [Fact]
    public void Diff_IgnoresRootPlacement()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        instance.Name = "Desk Lamp";
        instance.Transform.Position = new Vector3(1f, 2f, 3f);

        Assert.Empty(Diff(Scene(instance)).Overrides);
    }

    /// <summary>Children and components the instance adds are reported as added, and everything under them.</summary>
    [Fact]
    public void Diff_ReportsAddedObjects()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var shade = new Node { Name = "Shade", Children = { new Node { Name = "Tassel" } } };
        instance.Children[0].Children.Add(shade);
        var audio = new MeshComponent();
        instance.Components.Add(audio);

        var diff = Diff(Scene(instance));

        Assert.Contains(shade.Id, diff.Added);
        Assert.Contains(shade.Children[0].Id, diff.Added);
        Assert.Contains(audio.Id, diff.Added);
        Assert.DoesNotContain(instance.Children[0].Id, diff.Added);
    }

    /// <summary>A change inside a nested prefab is an override of the outer instance.</summary>
    [Fact]
    public void Diff_ReportsOverrideInsideNestedPrefab()
    {
        var lampId = AddPrefab(Lamp());
        var room = new Node { Name = "Room" };
        room.Children.Add(Instantiate(lampId));
        var roomId = Guid.NewGuid();
        prefabs[roomId] = Save(room);

        var instance = Instantiate(roomId);
        var light = instance.Children[0].Children[0].GetComponent<LightComponent>()!;
        light.Intensity = 9f;

        Assert.Contains((light.Id, nameof(LightComponent.Intensity)), Diff(Scene(instance)).Overrides);
    }

    /// <summary>An instance added inside another instance is compared with its own prefab.</summary>
    [Fact]
    public void Diff_ComparesAddedInstanceWithItsOwnPrefab()
    {
        var lampId = AddPrefab(Lamp());
        var outer = Instantiate(lampId);
        var inner = Instantiate(lampId);
        outer.Children.Add(inner);
        var light = inner.Children[0].GetComponent<LightComponent>()!;
        light.Intensity = 5f;

        var diff = Diff(Scene(outer));

        Assert.Contains(inner.Id, diff.Added);
        Assert.Contains((light.Id, nameof(LightComponent.Intensity)), diff.Overrides);
    }

    /// <summary>An instance whose prefab is gone is reported, not compared.</summary>
    [Fact]
    public void Diff_ReportsMissingPrefab()
    {
        var prefabId = AddPrefab(Lamp());
        var instance = Instantiate(prefabId);
        prefabs.Remove(prefabId);

        var diff = Diff(Scene(instance));

        Assert.Equal([instance.Id], diff.MissingPrefabs);
        Assert.Empty(diff.Overrides);
    }
}
