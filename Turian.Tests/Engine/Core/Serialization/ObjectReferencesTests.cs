namespace Turian.Tests;

/// <summary>
/// Tests that members typed as a node, component or DataAsset serialize as references and resolve after load,
/// instead of being written as inline copies.
/// </summary>
public sealed class ObjectReferencesTests : IDisposable
{
    /// <summary>A component holding every kind of direct reference.</summary>
    [TypeId("a4000004-0000-4000-8000-000000000001")]
    public sealed class Linker : Component
    {
        /// <summary>A node reference.</summary>
        public Node? Target { get; set; }

        /// <summary>A component reference.</summary>
        public CameraComponent? Camera;

        /// <summary>A list of node references.</summary>
        public List<Node?>? Waypoints { get; set; }

        /// <summary>A DataAsset reference.</summary>
        public Stats? Data { get; set; }
    }

    /// <summary>A DataAsset that references another DataAsset.</summary>
    [TypeId("a4000004-0000-4000-8000-000000000002")]
    public sealed class Stats : DataAsset
    {
        /// <summary>A plain value.</summary>
        public int Health { get; set; }

        /// <summary>Another DataAsset.</summary>
        public Stats? Next { get; set; }
    }

    readonly string projectRoot = Path.Combine(Path.GetTempPath(), $"turian-refs-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose()
    {
        RuntimeServices.Reset();
        TestAssetDatabase.Reset();
        if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
    }

    /// <summary>Verifies that scene references are written as ids and resolve to the loaded objects.</summary>
    [Fact]
    public void SceneReferences_RoundTripToTheLoadedObjects()
    {
        var (root, linker, target, camera) = BuildScene();
        linker.Waypoints = [target, null, root];

        var json = Serializer.Serialize(root);
        var loaded = Serializer.LoadData<Node>(json)!;

        Assert.Contains($"\"$ref\": \"{target.Id}\"", json);
        var loadedLinker = loaded.Children[0].GetComponent<Linker>()!;
        var loadedTarget = loaded.Children[1];
        Assert.Same(loadedTarget, loadedLinker.Target);
        Assert.Same(loadedTarget.GetComponent<CameraComponent>(), loadedLinker.Camera);
        Assert.Equal([loadedTarget, null, loaded], loadedLinker.Waypoints!);
        Assert.NotSame(target, loadedLinker.Target);
        Assert.NotNull(camera);
    }

    /// <summary>Verifies that a reference whose target is missing survives a load and save.</summary>
    [Fact]
    public void MissingTarget_IsKeptOnResave()
    {
        var (root, linker, _, _) = BuildScene();
        var missing = new Node { Name = "Elsewhere" };
        linker.Target = missing;

        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(root))!;
        var loadedLinker = loaded.Children[0].GetComponent<Linker>()!;

        Assert.Null(loadedLinker.Target);
        Assert.Contains($"\"$ref\": \"{missing.Id}\"", Serializer.Serialize(loaded));

        ObjectReferences.Forget(loadedLinker, nameof(Linker.Target));
        Assert.DoesNotContain(missing.Id.ToString(), Serializer.Serialize(loaded));
    }

    /// <summary>Verifies that a destroyed target is saved as null, like a missing one.</summary>
    [Fact]
    public void DestroyedTargets_AreSavedAsNull()
    {
        var (root, linker, target, camera) = BuildScene();
        target.IsDestroyed = true;
        camera.Detach();

        var json = Serializer.Serialize(root);

        Assert.True(camera.IsDestroyed);
        Assert.Contains("\"Target\": null", json);
        Assert.Contains("\"Camera\": null", json);
        Assert.True(ObjectReferences.IsMissing(linker.Target));
    }

    /// <summary>Verifies that a reference into another scene resolves once that scene is loaded too.</summary>
    [Fact]
    public void CrossSceneReference_ResolvesWhenBothScenesAreLoaded()
    {
        TestAssetDatabase.Reset();
        var scenes = new SceneManager(new AssetDatabase());
        var other = new Node { Name = "Other" };
        var holder = new Node { Name = "Holder" };
        holder.AddComponent(new Linker { Target = other });

        var loadedHolder = Serializer.LoadData<Node>(Serializer.Serialize(holder))!;
        scenes.AdoptScene(Guid.NewGuid(), loadedHolder);
        Assert.Null(loadedHolder.GetComponent<Linker>()!.Target);

        scenes.AdoptScene(Guid.NewGuid(), other, LoadSceneMode.Additive);

        Assert.Same(other, loadedHolder.GetComponent<Linker>()!.Target);
    }

    /// <summary>
    /// Verifies that the inspector sees a list element's pending id, and that clearing the element drops it.
    /// </summary>
    [Fact]
    public void ListElement_PendingIdIsShownAndCleared()
    {
        var (root, linker, target, _) = BuildScene();
        var missing = new Node();
        linker.Waypoints = [missing, target];
        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(root))!;
        var loadedLinker = loaded.Children[0].GetComponent<Linker>()!;

        var list = CollectionField.TryCreate(FormBuilder.Build(loadedLinker).Sections
            .SelectMany(static section => section.Fields).First(static f => f.Name == nameof(Linker.Waypoints)))!;
        var first = ReferenceField.TryCreate(list.Entries()[0])!;

        Assert.Equal(missing.Id, first.CurrentId);
        Assert.True(first.Clear());
        Assert.Equal(Guid.Empty, first.CurrentId);
        Assert.DoesNotContain(missing.Id.ToString(), Serializer.Serialize(loaded));
    }

    /// <summary>Verifies that a scene saved with an inline copy still loads.</summary>
    [Fact]
    public void LegacyInlineValue_StillLoads()
    {
        var linker = new Linker();
        var node = new Node { Name = "Holder" };
        node.AddComponent(linker);

        var json = Serializer.Serialize(node).Replace("\"Target\": null",
            $"\"Target\": {Serializer.Serialize(new Node { Name = "Inline" })}");
        var loaded = Serializer.LoadData<Node>(json)!;

        Assert.Equal("Inline", loaded.GetComponent<Linker>()!.Target!.Name);
    }

    /// <summary>
    /// Verifies that DataAsset references resolve to the loader's shared instances, including a cycle.
    /// </summary>
    [Fact]
    public async Task DataAssetReferences_ResolveThroughTheLoader()
    {
        TestAssetDatabase.Reset();
        var database = new AssetDatabase();
        var assets = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assets);

        var a = Register(database, assets, "A.asset");
        var b = Register(database, assets, "B.asset");
        Serializer.Save<DataAsset>(Path.Combine(assets, "A.asset"),
            new Stats { Health = 1, Next = new Stats { Id = b.Id } });
        Serializer.Save<DataAsset>(Path.Combine(assets, "B.asset"),
            new Stats { Health = 2, Next = new Stats { Id = a.Id } });

        var loader = new RuntimeAssetLoader(database);
        var first = await loader.LoadContentAsync<Stats>(a.Id);

        Assert.NotNull(first);
        Assert.Equal(2, first.Next!.Health);
        Assert.Same(first, first.Next.Next);
        Assert.Same(first.Next, await loader.LoadContentAsync<Stats>(b.Id));

        RuntimeServices.Configure(new ServiceCollection().AddSingleton<IAssetLoader>(loader).BuildServiceProvider());
        var node = new Node();
        node.AddComponent(new Linker { Data = first });
        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(node))!;

        Assert.Same(first, loaded.GetComponent<Linker>()!.Data);
    }

    static (Node Root, Linker Linker, Node Target, CameraComponent Camera) BuildScene()
    {
        var root = new Node { Name = "Root" };
        var holder = new Node { Name = "Holder" };
        var target = new Node { Name = "Target" };
        var camera = new CameraComponent();
        var linker = new Linker();

        target.AddComponent(camera);
        holder.AddComponent(linker);
        root.Children.Add(holder);
        root.Children.Add(target);

        linker.Target = target;
        linker.Camera = camera;
        return (root, linker, target, camera);
    }

    static DataAssetAsset Register(AssetDatabase database, string assets, string name)
    {
        var path = Path.Combine(assets, name);
        var meta = new DataAssetAsset { RelativePath = path };
        File.WriteAllText($"{path}.meta", Serializer.Serialize(meta));
        Assert.True(database.RegisterAsset(meta));
        return meta;
    }
}
