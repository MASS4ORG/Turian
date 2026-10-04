namespace Turian.Tests;

/// <summary>Checks stable layer indices, compatible serialization and indexed scene tag membership.</summary>
public sealed class TagsAndLayersTests
{
    const string LegacyNode = """
        {"__TypeId":"f7cc675d-54f2-510b-8e5d-807281e3012d","Name":"Old Root","Children":[
          {"__TypeId":"f7cc675d-54f2-510b-8e5d-807281e3012d","Name":"Old Child"}]}
        """;

    static ServiceProvider Services(AppSettings settings) => new ServiceCollection()
        .AddSingleton<IAppSettings>(settings).AddSingleton<LayerFilter>().BuildServiceProvider();

    /// <summary>Both layer spaces have 32 distinct, repeatable identities and a Default slot at zero.</summary>
    [Fact]
    public void DefaultsHaveIndependentStableIdentities()
    {
        var settings = new TagsAndLayersSettings();
        Assert.Equal(["Untagged"], settings.Tags);
        Assert.Equal(32, settings.PhysicsLayers.Count);
        Assert.Equal(32, settings.RenderLayers.Count);
        Assert.Equal("Default", settings.FindPhysicsLayer(0)!.Name);
        Assert.Equal("Default", settings.FindRenderLayer(0)!.Name);
        Assert.Equal(64, settings.PhysicsLayers.Concat(settings.RenderLayers).Select(layer => layer.Id).Distinct().Count());
        Assert.Equal(settings.PhysicsLayers.Select(layer => layer.Id),
            new TagsAndLayersSettings().PhysicsLayers.Select(layer => layer.Id));
        Assert.Empty(settings.Validate());
    }

    /// <summary>Renaming and reordering settings preserve the indices and GUIDs serialized nodes resolve to.</summary>
    [Fact]
    public void RenamingAndReorderingSurviveSettingsAndSceneRoundTrips()
    {
        var project = new AppSettings();
        var settings = project.Get<TagsAndLayersSettings>();
        var physicsId = settings.FindPhysicsLayer(17)!.Id;
        var renderId = settings.FindRenderLayer(31)!.Id;
        var json = Serializer.Serialize(new Node { PhysicsLayer = 17, RenderLayer = 31, Tags = ["Player", "State.Burn"] });
        settings.FindPhysicsLayer(17)!.Name = "Characters";
        settings.FindRenderLayer(31)!.Name = "Effects";
        settings.PhysicsLayers.Reverse();
        settings.RenderLayers.Reverse();
        project.Loaded.Use(Serializer.LoadData<TagsAndLayersSettings>(Serializer.Serialize(settings))!);
        using var services = Services(project);
        var node = Serializer.LoadData<Node>(json)!;
        node.Awake(null, services);

        Assert.Equal(17, node.PhysicsLayer);
        Assert.Equal(31, node.RenderLayer);
        Assert.Equal(physicsId, node.PhysicsLayerId);
        Assert.Equal(renderId, node.RenderLayerId);
        Assert.Equal(["Player", "State.Burn"], node.Tags);
        Assert.Equal("Characters", project.Get<TagsAndLayersSettings>().FindPhysicsLayer(17)!.Name);
        Assert.DoesNotContain("PhysicsLayerId", json);
        Assert.DoesNotContain("RenderLayerId", json);
    }

    /// <summary>Scenes and compact prefab instances missing tag and layer fields load with defaults without rewriting.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySceneAndPrefabLoadUnmodified(bool prefab)
    {
        var directory = Directory.CreateTempSubdirectory("turian-layer-legacy-");
        try
        {
            var path = Path.Combine(directory.FullName, "legacy.prefab");
            File.WriteAllText(path, LegacyNode);
            var manager = new SceneManager(new AssetDatabase());
            var root = prefab ? await manager.InstantiateAsync(path) : (await manager.LoadSceneAsync(path)).RootNode;
            foreach (var node in new[] { root, root.Children[0] })
            {
                Assert.Equal(["Untagged"], node.Tags);
                Assert.Equal(0, node.PhysicsLayer);
                Assert.Equal(0, node.RenderLayer);
                Assert.NotEqual(Guid.Empty, node.PhysicsLayerId);
            }

            Assert.Equal(LegacyNode, File.ReadAllText(path));
            var compact = PrefabInstances.CreateInstanceJson(Guid.NewGuid(), Guid.NewGuid());
            var expanded = PrefabInstances.Expand(compact, _ => LegacyNode);
            var instance = Serializer.LoadData<Node>(expanded)!;
            Assert.Equal(["Untagged"], instance.Tags);
            Assert.Equal(0, instance.Children[0].RenderLayer);
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>Empty explicitly saved tags stay empty; omitted tags get the legacy default.</summary>
    [Fact]
    public void ExplicitEmptyTagsRemainEmpty()
    {
        var node = Serializer.LoadData<Node>(Serializer.Serialize(new Node { Tags = [] }))!;
        node.Awake(null);
        Assert.Empty(node.Tags);
        Assert.Empty(node.FindAllWithTag("Untagged"));
        Assert.Null(node.FindWithTag(null));
    }

    /// <summary>Active queries preserve registration order through tag replacement, duplicates and activation.</summary>
    [Fact]
    public void TagChangesAndActivityUpdateIndexedQueries()
    {
        var first = new Node { Tags = ["Enemy", "Enemy"] };
        var second = new Node { Tags = ["Enemy"] };
        var root = new Node { Children = [first, second] };
        root.Awake(null);
        Assert.Equal([first, second], root.FindAllWithTag("Enemy"));
        first.Tags.Remove("Enemy");
        Assert.Same(first, root.FindWithTag("Enemy"));
        first.Tags[0] = "Friend";
        Assert.Same(second, root.FindWithTag("Enemy"));
        first.Tags = ["Enemy"];
        Assert.Equal([first, second], root.FindAllWithTag("Enemy"));
        first.IsActive = false;
        Assert.Equal([second], root.FindAllWithTag("Enemy"));
        root.IsActive = false;
        Assert.Empty(root.FindAllWithTag("Enemy"));
        root.IsActive = true;
        first.IsActive = true;
        Assert.Equal([first, second], root.FindAllWithTag("Enemy"));
        first.Tags.Clear();
        second.Tags.Clear();
        Assert.Null(root.FindWithTag("Enemy"));
    }

    /// <summary>Reparent, collection replacement, removal, destruction and additive unload maintain separate registries.</summary>
    [Fact]
    public void RegistryTracksReparentDestroyAndUnload()
    {
        var manager = new SceneManager(new AssetDatabase());
        var first = new Node();
        var second = new Node();
        var node = new Node { Tags = ["Player"], Children = [new Node { Tags = ["Player"] }] };
        first.Children.Add(node);
        var a = manager.AdoptScene(Guid.NewGuid(), first);
        var b = manager.AdoptScene(Guid.NewGuid(), second, LoadSceneMode.Additive);
        var nested = node.Children[0];
        Assert.Equal([node, nested], manager.FindAllWithTag("Player"));
        Assert.Same(node, manager.FindWithTag("Player"));

        second.Children.Add(node);
        Assert.Empty(first.FindAllWithTag("Player"));
        Assert.Equal([node, nested], second.FindAllWithTag("Player"));
        Assert.DoesNotContain(node, first.Children);
        second.Children.Move(0, 0);
        Assert.Equal([node, nested], second.FindAllWithTag("Player"));
        second.Children = [];
        Assert.Empty(second.FindAllWithTag("Player"));
        second.Children = [node];
        nested.IsDestroyed = true;
        Assert.Equal([node], manager.FindAllWithTag("Player"));

        manager.UnloadScene(a);
        Assert.Equal([node], manager.FindAllWithTag("Player"));
        manager.UnloadScene(b);
        Assert.Empty(manager.FindAllWithTag("Player"));
        Assert.Null(manager.FindWithTag("Player"));
        Assert.True(node.IsDestroyed);
        Assert.True(nested.IsDestroyed);
        Assert.Empty(second.FindAllWithTag("Player"));
    }

    /// <summary>Queries use registered entries and cached activity without accessing the scene tree.</summary>
    [Fact]
    public void QueriesDoNotEnumerateChildrenOrAncestors()
    {
        var root = new Node();
        var parent = root;
        for (var i = 0; i < 256; i++)
        {
            var next = new Node();
            parent.Children.Add(next);
            parent = next;
        }

        parent.Tags = ["Deep"];
        root.Awake(null);
        // Removing the backing collection without a change event makes a tree walk unable to find the node.
        var backing = typeof(Node).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(System.Collections.ObjectModel.ObservableCollection<Node>));
        backing.SetValue(root, new System.Collections.ObjectModel.ObservableCollection<Node>());
        Assert.Same(parent, root.FindWithTag("Deep"));
        Assert.Equal([parent], root.FindAllWithTag("Deep"));
        Assert.Empty(root.FindAllWithTag("Missing"));
    }

    /// <summary>Unnamed and missing indices fall back to Default in the matching space.</summary>
    [Fact]
    public void OrphanedIndicesMigrateToDefault()
    {
        var project = new AppSettings();
        var settings = project.Get<TagsAndLayersSettings>();
        settings.PhysicsLayers = [.. settings.PhysicsLayers.Take(10)];
        settings.RenderLayers[17].Name = "";
        using var services = Services(project);
        var root = new Node { PhysicsLayer = 17, RenderLayer = 17 };
        root.Awake(null, services);
        Assert.Equal(0, root.PhysicsLayer);
        Assert.Equal(0, root.RenderLayer);
        Assert.Equal(settings.FindPhysicsLayer(0)!.Id, root.PhysicsLayerId);
        Assert.Equal(settings.FindRenderLayer(0)!.Id, root.RenderLayerId);
        root.PhysicsLayer = -1;
        root.RenderLayer = 99;
        Assert.Equal(0, root.PhysicsLayer);
        Assert.Equal(0, root.RenderLayer);
        settings.PhysicsLayers.Clear();
        Assert.Equal("Default", services.GetRequiredService<LayerFilter>().ResolvePhysics(0).Name);
    }

    /// <summary>Invalid settings report all validation categories, while valid settings report none.</summary>
    [Fact]
    public void ValidationReportsInvalidNamesIndicesAndIdentities()
    {
        var settings = new TagsAndLayersSettings { Tags = ["", "Player", "Player"] };
        settings.PhysicsLayers.Add(new LayerSlot { Index = 32, Name = "" });
        settings.PhysicsLayers.Add(settings.PhysicsLayers[1]);
        settings.RenderLayers.Clear();
        var warnings = settings.Validate();
        Assert.Contains(warnings, warning => warning.Contains("empty", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("duplicated", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("between 0 and 31", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("layer 0", StringComparison.Ordinal));
        Assert.Null(settings.FindPhysicsLayer(-1));
        Assert.Null(settings.FindRenderLayer(32));
    }
}
