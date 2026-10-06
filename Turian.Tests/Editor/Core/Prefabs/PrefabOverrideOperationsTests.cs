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
        database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!,
            assetLoader: loader, sceneManager: Substitute.For<ISceneManager>(), database: database);
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

    /// <summary>Copied instances retain inherited identities and their real overrides, including added nested instances.</summary>
    [Fact]
    public void DuplicateInstances_PreservesPrefabLinksAndOverrides()
    {
        var prefab = Lamp();
        var id = AddPrefab(prefab);
        var first = Instantiate(id);
        var second = Instantiate(id);
        first.Children[0].GetComponent<LightComponent>()!.Intensity = 7f;
        var nested = Instantiate(id);
        root.Children.Remove(nested);
        first.Children.Add(nested);
        root.Awake(null);

        var copies = sceneTree.DuplicateNodes([first, second]);

        Assert.NotEqual(first.Id, copies[0].Id);
        Assert.NotEqual(second.Id, copies[1].Id);
        Assert.Equal(PrefabInstances.DeriveId(copies[0].Id, prefab.Children[0].Id), copies[0].Children[0].Id);
        var light = copies[0].Children[0].GetComponent<LightComponent>()!;
        Assert.Equal(PrefabInstances.DeriveId(copies[0].Id, prefab.Children[0].Components[0].Id), light.Id);
        Assert.Equal(7f, light.Intensity);
        var diff = operations.DiffFor(copies[0])!;
        Assert.Single(diff.Overrides);
        Assert.Equal(operations.DiffFor(first)!.Added.Count, diff.Added.Count);
        Assert.Empty(diff.Removed);
        Assert.Empty(operations.DiffFor(copies[1])!.Overrides);
        Assert.Empty(operations.DiffFor(copies[1])!.Added);
        Assert.Empty(operations.DiffFor(copies[1])!.Removed);
        var nestedCopy = copies[0].Children[2];
        Assert.NotEqual(nested.Id, nestedCopy.Id);
        Assert.Equal(PrefabInstances.DeriveId(nestedCopy.Id, prefab.Children[0].Id), nestedCopy.Children[0].Id);
        var nestedDiff = PrefabInstances.Diff(Serializer.Serialize(nestedCopy),
            assetId => PrefabInstances.ReadPrefabJson(database, assetId));
        Assert.Empty(nestedDiff.Overrides);
        Assert.Empty(nestedDiff.Added);
        Assert.Empty(nestedDiff.Removed);

        var compact = PrefabInstances.Compact(Serializer.Serialize(copies[1]),
            assetId => PrefabInstances.ReadPrefabJson(database, assetId));
        prefab.Children[0].GetComponent<LightComponent>()!.Intensity = 3f;
        File.WriteAllText(Path.Combine(projectRoot, $"Assets/{id:N}.prefab"), Serializer.Serialize(prefab));
        var expanded = Serializer.LoadData<Node>(PrefabInstances.Expand(compact,
            assetId => PrefabInstances.ReadPrefabJson(database, assetId)))!;
        Assert.Equal(3f, expanded.Children[0].GetComponent<LightComponent>()!.Intensity);
    }

    /// <summary>A protected multi-selection unpacks each instance and deletes every selected child in one undo step.</summary>
    [Fact]
    public void HierarchyMultiDeleteConfirmsAndUnpacksEachInstance()
    {
        var id = AddPrefab(Lamp());
        var first = Instantiate(id);
        var second = Instantiate(id);
        var selection = new NodeInspectorController(assets);
        selection.SelectMany([first.Children[0], second.Children[0]]);
        var localization = new StudioLocalization();
        var confirm = new ConfirmDialogChrome(localization);
        var panel = new SceneTreePanel(sceneTree, selection, assets, null!, null!, new SettingsService(),
            undo, operations, confirm, localization, database);
        panel.DeleteSelected();
        Assert.Equal(2, first.Children.Count);
        Assert.Equal(2, second.Children.Count);
        var continuation = (Action)typeof(ConfirmDialogChrome)
            .GetField("confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(confirm)!;
        continuation();
        Assert.Single(first.Children);
        Assert.Single(second.Children);
        Assert.Null(first.PrefabInstance);
        Assert.Null(second.PrefabInstance);
        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.Equal(2, first.Children.Count);
        Assert.Equal(2, second.Children.Count);
        Assert.NotNull(first.PrefabInstance);
        Assert.NotNull(second.PrefabInstance);
    }

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
        instance.Children[0].Position = new Vector3(3f, 0f, 0f);
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
