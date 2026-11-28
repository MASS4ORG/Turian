namespace Turian.Tests;

/// <summary>Opens the menu of a field a prefab instance overrides.</summary>
[Collection(SerialTests.Name)]
public sealed class InspectorOverrideMenuTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-override-menu-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Right-clicking an overridden component value offers to revert it to the prefab's.</summary>
    [Fact]
    public void AnOverriddenValueCanBeReverted()
    {
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var selection = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, selection, assets, loader);
        var operations = new PrefabOverrideOperations(tree, undo, null!, database, loader);
        var scene = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
        assets.OpenAsset(scene);
        tree.OpenAsset(scene);
        var sceneRoot = new Node { Name = "Scene" };
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, sceneRoot);
        var prefabId = AddPrefab(database,
            new Node { Name = "Lamp", Components = { new LightComponent { Intensity = 1f } } });
        var instance = Serializer.LoadData<Node>(PrefabInstances.Expand(
            PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()),
            id => PrefabInstances.ReadPrefabJson(database, id)))!;
        sceneRoot.Children.Add(instance);
        sceneRoot.Awake(null);
        var light = instance.GetComponent<LightComponent>()!;
        light.Intensity = 7f;
        selection.Select(instance);
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!), null!,
            null!, new InspectorSettings(), null!, null!, undo, null!, operations, null!, database);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns("");
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(640, 900));
        var font = Font.FromFamilyName("sans-serif", 14);
        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        void Click(Vector2 position, GMouseButton button)
        {
            input.MousePosition.Returns(position);
            Frame();
            input.IsMouseButtonPressed(button).Returns(true);
            input.IsMouseButtonDown(button).Returns(true);
            Frame();
            input.IsMouseButtonPressed(button).Returns(false);
            input.IsMouseButtonDown(button).Returns(false);
            Frame();
        }

        Frame();
        Frame();
        var intensity = Array.FindIndex([.. InspectorForms.BuildForNode(instance).Sections[1].BodyFields],
            static field => field.Name == nameof(LightComponent.Intensity));
        var row = Nodes(gui.RootNode!).Single(node => node.Id == $"inspector/section1/field{intensity}/prefab").Rect;
        var position = new Vector2(row.X + 20, row.Y + row.H / 2);

        Click(position, GMouseButton.Right);
        Click(position + new Vector2(14, 12), GMouseButton.Left);

        Assert.Equal(1f, instance.GetComponent<LightComponent>()!.Intensity);
    }

    static IEnumerable<LayoutNode> Nodes(LayoutNode node) => new[] { node }.Concat(node.Children.SelectMany(Nodes));

    Guid AddPrefab(AssetDatabase database, Node prefab)
    {
        var id = Guid.NewGuid();
        var relative = $"Assets/{id:N}.prefab";
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serializer.Serialize(prefab));
        database.MergeDatabase(new Dictionary<Guid, AssetRecord>
        {
            [id] = new()
            {
                AssetId = id,
                ProjectRootPath = root,
                AssetTypeName = typeof(Prefab).FullName!,
                SourceRelativePath = relative,
                ImportedRelativePath = relative,
                StorageKind = AssetStorageKind.LooseFile,
            },
        });
        return id;
    }
}
