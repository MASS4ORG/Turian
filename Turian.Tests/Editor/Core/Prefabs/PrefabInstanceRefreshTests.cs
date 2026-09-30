namespace Turian.Tests;

/// <summary>Tests for updating an open scene's instances when their prefab is saved.</summary>
public class PrefabInstanceRefreshTests
{
    readonly Dictionary<Guid, string> prefabs = [];

    [TypeId("156c1f2d-b68f-4634-a84a-d84d494313d1")]
    sealed class RuleConsumer : Component
    {
        public DataAssetTest? Rule { get; set; }

        [JsonIgnore]
        public bool HadRuleAtAwake { get; private set; }

        public override void OnAwake() => HadRuleAtAwake = Rule is not null;
    }

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

        var rebuilt = PrefabInstanceRefresh.Rebuild(scene, prefabId, previous, Load,
            Substitute.For<IAssetLoader>())!;

        Assert.Equal(2f, Intensity(rebuilt.Children[0]));
        Assert.Equal(5f, Intensity(rebuilt.Children[1]));
        Assert.Equal("Shade", rebuilt.Children[0].Children[1].Name);
        Assert.Equal(plain.Id, rebuilt.Children[0].Id);
        Assert.Same(rebuilt, rebuilt.Children[0].Parent);
    }

    /// <summary>Rebuilt prefab components receive direct DataAsset fields before Awake.</summary>
    [Fact]
    public void Rebuild_UsesTheProvidedAssetLoaderForDirectFields()
    {
        var ruleId = Guid.NewGuid();
        var lamp = Lamp(1f);
        lamp.Children[0].Components.Add(new RuleConsumer { Rule = new DataAssetTest { Id = ruleId } });
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = Serializer.Serialize(lamp);
        var scene = new Node { Name = "Scene", Children = { Instantiate(prefabId) } };
        var previous = prefabs[prefabId];
        lamp.Children[0].GetComponent<LightComponent>()!.Intensity = 2f;
        prefabs[prefabId] = Serializer.Serialize(lamp);

        var loader = Substitute.For<IAssetLoader>();
        var rule = new DataAssetTest { Id = ruleId };
        loader.PreloadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        loader.LoadContentAsync<DataAsset>(ruleId).Returns(Task.FromResult<DataAsset?>(rule));

        var rebuilt = PrefabInstanceRefresh.Rebuild(scene, prefabId, previous, Load, loader)!;

        var consumer = rebuilt.Children[0].Children[0].GetComponent<RuleConsumer>()!;
        Assert.True(consumer.HadRuleAtAwake);
        Assert.Same(rule, consumer.Rule);
    }

    /// <summary>A scene with no instances is left alone.</summary>
    [Fact]
    public void Rebuild_SceneWithoutInstances_ReturnsNull()
    {
        var prefabId = Guid.NewGuid();
        prefabs[prefabId] = Serializer.Serialize(Lamp(1f));

        Assert.Null(PrefabInstanceRefresh.Rebuild(new Node { Name = "Scene" }, prefabId, prefabs[prefabId], Load,
            Substitute.For<IAssetLoader>()));
    }
}
