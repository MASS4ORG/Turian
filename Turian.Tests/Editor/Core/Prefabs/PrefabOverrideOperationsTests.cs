namespace Turian.Tests;

/// <summary>Tests for reverting and unpacking prefab instances in the open scene.</summary>
public class PrefabOverrideOperationsTests : IDisposable
{
    readonly string projectRoot = Path.Combine(Path.GetTempPath(), $"turian-overrides-{Guid.NewGuid():N}");
    readonly AssetManager assets = new();
    readonly AssetDatabase database;
    readonly SceneTreeController sceneTree;
    readonly UndoService undo;
    readonly PrefabOverrideOperations operations;
    readonly Node root = new() { Name = "Scene" };

    /// <summary>Opens a scene backed by a throwaway project; applies are not exercised, so no importer is needed.</summary>
    public PrefabOverrideOperationsTests()
    {
        TestAssetDatabase.Reset();
        database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!,
            assetLoader: loader);
        var inspector = new NodeInspectorController(assets);
        undo = new UndoService(sceneTree, inspector, assets, loader);
        operations = new PrefabOverrideOperations(sceneTree, undo, importer: null!, database, loader);

        var scene = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
        assets.OpenAsset(scene);
        sceneTree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
    }

    /// <summary>Deletes the project and the database singleton.</summary>
    public void Dispose()
    {
        undo.Dispose();
        TestAssetDatabase.Reset();
        if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    Guid AddPrefab(Node prefab)
    {
        var id = Guid.NewGuid();
        var relative = $"Assets/{id:N}.prefab";
        var path = Path.Combine(projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serializer.Serialize(prefab));
        database.MergeDatabase(new Dictionary<Guid, AssetRecord>
        {
            [id] = new()
            {
                AssetId = id,
                ProjectRootPath = projectRoot,
                AssetTypeName = typeof(Prefab).FullName!,
                SourceRelativePath = relative,
                ImportedRelativePath = relative,
                StorageKind = AssetStorageKind.LooseFile,
            },
        });
        return id;
    }

    Node Instantiate(Guid prefabId)
    {
        var instance = Serializer.LoadData<Node>(PrefabInstances.Expand(
            PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            id => PrefabInstances.ReadPrefabJson(database, id)))!;
        root.Children.Add(instance);
        root.Awake(null);
        return instance;
    }

    static Node Lamp() => new()
    {
        Name = "Lamp",
        Children =
        {
            new Node { Name = "Bulb", Components = { new LightComponent { Intensity = 1f } } },
            new Node { Name = "Base" },
        },
    };

    /// <summary>A reverted value takes the prefab's again, and undo brings the override back.</summary>
    [Fact]
    public void RevertMember_RestoresThePrefabValue()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var light = instance.Children[0].GetComponent<LightComponent>()!;
        light.Intensity = 7f;

        Assert.True(operations.RevertMember(light, nameof(LightComponent.Intensity)));
        Assert.Equal(1f, light.Intensity);

        undo.Flush();
        undo.Undo();
        Assert.Equal(7f, light.Intensity);
    }

    /// <summary>Revert All undoes overrides, drops additions and brings removed prefab objects back.</summary>
    [Fact]
    public void RevertAll_MatchesThePrefabAgain()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        instance.Children[0].GetComponent<LightComponent>()!.Intensity = 7f;
        instance.Children.Add(new Node { Name = "Added" });
        var removed = instance.Children[1];
        instance.Children.Remove(removed);

        operations.RevertAll(instance);

        Assert.Equal(["Bulb", "Base"], instance.Children.Select(child => child.Name));
        Assert.Equal(removed.Id, instance.Children[1].Id);
        Assert.Equal(1f, instance.Children[0].GetComponent<LightComponent>()!.Intensity);
        Assert.Empty(operations.DiffFor(instance)!.Overrides);
    }

    /// <summary>A component the instance added is removed by reverting it.</summary>
    [Fact]
    public void RevertComponent_RemovesAnAddedComponent()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var added = instance.Children[1].AddComponent(new MeshComponent());

        operations.RevertComponent(added);

        Assert.Empty(instance.Children[1].Components);
    }

    /// <summary>Unpacking keeps the hierarchy and drops the link; completely also drops the nested links.</summary>
    [Fact]
    public void Unpack_BreaksTheLink()
    {
        var lampId = AddPrefab(Lamp());
        var instance = Instantiate(lampId);
        var nested = Serializer.LoadData<Node>(PrefabInstances.Expand(
            PrefabInstances.CreateInstanceJson(lampId, Guid.NewGuid()),
            id => PrefabInstances.ReadPrefabJson(database, id)))!;
        instance.Children.Add(nested);

        operations.Unpack(instance, completely: false);
        Assert.Null(instance.PrefabInstance);
        Assert.NotNull(nested.PrefabInstance);

        operations.Unpack(instance, completely: true);
        Assert.Null(nested.PrefabInstance);
        Assert.Equal(3, instance.Children.Count);
    }

    /// <summary>Revert All is one step: undo gives back the overrides, the additions and the removals.</summary>
    [Fact]
    public void RevertAll_IsUndoneInOneStep()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        instance.Children[0].Name = "Bulb (tuned)";
        instance.Children[0].Transform.Position = new Vector3(3f, 0f, 0f);
        var added = new Node { Name = "Added" };
        instance.Children.Add(added);

        operations.RevertAll(instance);
        undo.Flush();
        undo.Undo();

        Assert.Equal("Bulb (tuned)", instance.Children[0].Name);
        Assert.Equal(3f, instance.Children[0].Transform.Position.X);
        Assert.Contains(added, instance.Children);
    }

    /// <summary>Unpacking to delete a prefab's object is one step: undo restores the link and the object.</summary>
    [Fact]
    public void UnpackAndDelete_IsOneStep()
    {
        var instance = Instantiate(AddPrefab(Lamp()));
        var bulb = instance.Children[0];

        undo.BeginGesture();
        operations.Unpack(instance, completely: false);
        undo.RecordObject(instance, "Delete");
        sceneTree.DetachNode(bulb);
        sceneTree.MarkAssetModified();
        undo.EndGesture();

        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.NotNull(instance.PrefabInstance);
        Assert.Contains(bulb, instance.Children);
    }
}
