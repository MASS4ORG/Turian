namespace Turian.Tests;

/// <summary>Tests for turning scene nodes into prefabs and prefabs into variants.</summary>
public class PrefabAuthoringTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    string? Load(Guid id) => prefabs.GetValueOrDefault(id);

    Node Instantiate(Guid prefabId, Guid instanceId) =>
        Serializer.LoadData<Node>(PrefabInstances.Expand(PrefabInstances.CreateInstanceJson(prefabId, instanceId),
            Load))!;

    static Node Lamp() => new()
    {
        Name = "Lamp",
        Children = { new Node { Name = "Bulb", Components = { new LightComponent { Intensity = 2f } } } },
    };

    static IEnumerable<Guid> Ids(Node node) =>
        [node.Id, .. node.Components.Select(component => component.Id), .. node.Children.SelectMany(Ids)];

    /// <summary>The converted node carries the ids loading an instance of the new prefab would give it.</summary>
    [Fact]
    public void LinkToPrefab_MatchesAFreshInstance()
    {
        var lamp = Lamp();
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = PrefabAuthoring.Serialize(lamp, Load);

        PrefabAuthoring.LinkToPrefab(lamp, prefabId);
        var fresh = Instantiate(prefabId, lamp.Id);

        Assert.Equal(prefabId, lamp.PrefabInstance!.Source.AssetId);
        Assert.Equal(Ids(fresh), Ids(lamp));
        Assert.Empty(PrefabInstances.Diff(Serializer.Serialize(new Node { Children = { lamp } }), Load).Overrides);
    }

    /// <summary>A node holding an instance of another prefab keeps it nested, with matching ids.</summary>
    [Fact]
    public void LinkToPrefab_KeepsNestedInstances()
    {
        var lampId = Guid.NewGuid();
        prefabs[lampId] = Serializer.Serialize(Lamp());
        var room = new Node { Name = "Room" };
        room.Children.Add(Instantiate(lampId, Guid.NewGuid()));
        var roomId = Guid.NewGuid();
        prefabs[roomId] = PrefabAuthoring.Serialize(room, Load);

        PrefabAuthoring.LinkToPrefab(room, roomId);
        var fresh = Instantiate(roomId, room.Id);

        Assert.NotNull(room.Children[0].PrefabInstance);
        Assert.Equal(Ids(fresh), Ids(room));
    }

    /// <summary>A variant is an unmodified instance of its prefab, under its own name.</summary>
    [Fact]
    public void VariantJson_ExpandsToThePrefabUnderItsName()
    {
        var lampId = Guid.NewGuid();
        prefabs[lampId] = Serializer.Serialize(Lamp());

        var variant = Serializer.LoadData<Node>(PrefabInstances.Expand(
            PrefabAuthoring.VariantJson(lampId, "Lamp Variant"), Load))!;

        Assert.Equal("Lamp Variant", variant.Name);
        Assert.Equal(lampId, variant.PrefabInstance!.Source.AssetId);
        Assert.Equal(2f, variant.Children[0].GetComponent<LightComponent>()!.Intensity);
    }
}
