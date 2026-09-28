namespace Turian.Tests;

/// <summary>Tests for how the scene tree tells prefab instances, nested ones, variants and broken links apart.</summary>
public class PrefabLinkClassifierTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    string? Load(Guid id) => prefabs.GetValueOrDefault(id);

    Guid AddPrefab(string json)
    {
        var id = Guid.NewGuid();
        prefabs[id] = json;
        return id;
    }

    Node Instantiate(Guid prefabId) =>
        Serializer.LoadData<Node>(PrefabInstances.Expand(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            Load))!;

    static Node Scene(params Node[] children)
    {
        var scene = new Node { Name = "Scene" };
        foreach (var child in children) scene.Children.Add(child);
        scene.Awake(null);
        return scene;
    }

    static string Lamp() => Serializer.Serialize(new Node { Name = "Lamp", Children = { new Node { Name = "Bulb" } } });

    /// <summary>An instance root is marked, the nodes its prefab provides take its color, the scene itself neither.</summary>
    [Fact]
    public void Classify_InstanceAndItsContent()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var scene = Scene(instance);
        var classifier = new PrefabLinkClassifier(Load);

        Assert.Equal(PrefabLink.None, classifier.Classify(scene));
        Assert.Equal(PrefabLink.Instance, classifier.Classify(instance));
        Assert.Equal(PrefabLink.InstanceContent, classifier.Classify(instance.Children[0]));
    }

    /// <summary>An instance under another instance is nested.</summary>
    [Fact]
    public void Classify_InstanceInsideInstanceIsNested()
    {
        var lampId = AddPrefab(Lamp());
        var outer = Instantiate(lampId);
        var inner = Instantiate(lampId);
        outer.Children.Add(inner);
        Scene(outer);

        Assert.Equal(PrefabLink.NestedInstance, new PrefabLinkClassifier(Load).Classify(inner));
    }

    /// <summary>An instance of a prefab whose root is itself an instance is an instance of a variant.</summary>
    [Fact]
    public void Classify_InstanceOfVariant()
    {
        var lampId = AddPrefab(Lamp());
        var variantId = AddPrefab(PrefabInstances.CreateInstanceJson(lampId, Guid.NewGuid()));
        var instance = Instantiate(variantId);
        Scene(instance);

        Assert.Equal(PrefabLink.VariantInstance, new PrefabLinkClassifier(Load).Classify(instance));
    }

    /// <summary>An instance whose prefab is gone is a broken link.</summary>
    [Fact]
    public void Classify_MissingPrefab()
    {
        var lampId = AddPrefab(Lamp());
        var instance = Instantiate(lampId);
        Scene(instance);
        prefabs.Remove(lampId);

        Assert.Equal(PrefabLink.Missing, new PrefabLinkClassifier(Load).Classify(instance));
    }
}
