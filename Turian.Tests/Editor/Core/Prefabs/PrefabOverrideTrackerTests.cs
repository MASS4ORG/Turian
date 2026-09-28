namespace Turian.Tests;

/// <summary>Tests for the inspector's view of what a selected prefab instance overrides.</summary>
public class PrefabOverrideTrackerTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    string? Load(Guid id) => prefabs.GetValueOrDefault(id);

    Guid AddPrefab(Node root)
    {
        var id = Guid.NewGuid();
        prefabs[id] = Serializer.Serialize(root);
        return id;
    }

    Node Instantiate(Guid prefabId) =>
        Serializer.LoadData<Node>(PrefabInstances.Expand(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            Load))!;

    static Node Lamp() => new()
    {
        Name = "Lamp",
        Children = { new Node { Name = "Bulb", Components = { new LightComponent { Intensity = 1f } } } },
    };

    /// <summary>A node inside nested instances belongs to the outermost one.</summary>
    [Fact]
    public void OutermostInstance_SkipsNestedInstances()
    {
        var lampId = AddPrefab(Lamp());
        var room = new Node { Name = "Room" };
        room.Children.Add(Instantiate(lampId));
        var roomId = Guid.NewGuid();
        prefabs[roomId] = PrefabInstances.Compact(Serializer.Serialize(room), Load);

        var instance = Instantiate(roomId);
        var scene = new Node { Name = "Scene", Children = { instance } };
        scene.Awake(null);
        var bulb = instance.Children[0].Children[0];

        Assert.Same(instance, PrefabOverrideTracker.OutermostInstance(bulb));
        Assert.Null(PrefabOverrideTracker.OutermostInstance(scene));
    }

    /// <summary>An edit shows up once the tracker is told the scene changed.</summary>
    [Fact]
    public void IsOverridden_FollowsEdits()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        new Node { Name = "Scene", Children = { instance } }.Awake(null);
        var light = instance.Children[0].GetComponent<LightComponent>()!;
        var tracker = new PrefabOverrideTracker(Load, TimeSpan.Zero);

        tracker.Track(instance.Children[0]);
        Assert.False(tracker.IsOverridden(light, nameof(LightComponent.Intensity)));

        light.Intensity = 4f;
        tracker.Invalidate();

        Assert.True(tracker.IsOverridden(light, nameof(LightComponent.Intensity)));
        Assert.Same(instance, tracker.InstanceRoot);
    }

    /// <summary>A node in no instance has nothing to compare.</summary>
    [Fact]
    public void Track_NodeOutsideInstances_HasNoDiff()
    {
        var tracker = new PrefabOverrideTracker(Load);

        tracker.Track(new Node { Name = "Loose" });

        Assert.Null(tracker.Diff);
        Assert.Null(tracker.InstanceRoot);
    }
}
