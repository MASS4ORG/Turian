namespace Turian.Tests;

/// <summary>Exercises composed forms and contextual actions through both headless Inspector passes.</summary>
public sealed class InspectorFormsRenderingTests
{
    /// <summary>Hierarchy modifier clicks keep their ordered selection when synchronized with the Inspector.</summary>
    [Fact]
    public void HierarchyRangeSelectionReachesInspector()
    {
        var assets = new AssetManager();
        var loader = Substitute.For<IAssetLoader>();
        var database = new AssetDatabase();
        var settings = new SettingsService();
        var sceneTree = new SceneTreeController(assets, settings, null!, loader,
            Substitute.For<ISceneManager>(), database);
        var selection = new NodeInspectorController(assets);
        using var undo = new UndoService(sceneTree, selection, assets, loader);
        var root = new Node();
        var nodes = Enumerable.Range(0, 10).Select(i => new Node { Parent = root, Name = $"Light {i}" }).ToArray();
        foreach (var node in nodes) root.Children.Add(node);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
        var panel = new SceneTreePanel(sceneTree, selection, assets, null!, null!, settings, undo,
            null!, null!, null!, database);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        var state = (TreeViewState)typeof(SceneTreePanel)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        state.SetExpanded("0", true);
        Frame(gui, surface, font, panel.Render);
        void ClickRow(int index)
        {
            var row = Descendants(gui.RootNode!).Single(node => node.Id == $"treeview/row{index}");
            input.MousePosition.Returns(new Vector2(row.Rect.X + 150, row.Rect.Y + row.Rect.H / 2));
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            Frame(gui, surface, font, panel.Render);
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
            Frame(gui, surface, font, panel.Render);
        }
        ClickRow(1);
        Assert.Equal([nodes[0]], selection.SelectedNodes);
        input.IsKeyDown(KeyboardKey.LeftShift).Returns(true);
        ClickRow(10);
        Assert.Equal(nodes, selection.SelectedNodes);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(nodes, selection.SelectedNodes);
        input.IsKeyDown(KeyboardKey.LeftShift).Returns(false);
        input.IsKeyDown(KeyboardKey.LeftControl).Returns(true);
        ClickRow(5);
        Assert.Equal(nodes.Where(node => !ReferenceEquals(node, nodes[4])), selection.SelectedNodes);
        input.IsKeyPressed(KeyboardKey.A).Returns(true);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(nodes.Prepend(root), selection.SelectedNodes);
        input.IsKeyPressed(KeyboardKey.A).Returns(false);
        input.IsKeyDown(KeyboardKey.LeftControl).Returns(false);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(true);
        Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(nodes.Prepend(root), selection.SelectedNodes);
        selection.SelectMany(nodes, nodes[0]);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(nodes, selection.SelectedNodes);
        var canDrop = typeof(SceneTreePanel).GetMethod("CanDrop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var componentPayload = new ScriptDragPayload(typeof(ModelComponent), "Model");
        Assert.True((bool)canDrop.Invoke(panel, [componentPayload])!);
        Assert.True((bool)canDrop.Invoke(panel, [new ReferenceDragPayload(nodes[0].Id, "Light")])!);
        Assert.False((bool)canDrop.Invoke(panel, [new object()])!);
        typeof(SceneTreePanel).GetMethod("Drop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [new TreeItem("0/0", "Light", 1, Tag: nodes[0]), componentPayload]);
        Assert.All(nodes, node => Assert.NotNull(node.GetComponent<ModelComponent>()));
        typeof(SceneTreePanel).GetMethod("Drop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [new TreeItem("0", "Root", 0, Tag: root), new ReferenceDragPayload(nodes[0].Id, "Light")]);
        Assert.All(nodes, node => Assert.Same(root, node.Parent));
    }

    /// <summary>Typing a mixed light intensity through the Inspector updates ten objects in one undoable edit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedInspectorEditUpdatesTenLightsAndUndoRestoresTheirValues(bool locked)
    {
        var assets = new AssetManager();
        var loader = Substitute.For<IAssetLoader>();
        var scene = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/test.prefab" };
        var sceneTree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), new AssetDatabase());
        var selection = new NodeInspectorController(assets);
        using var undo = new UndoService(sceneTree, selection, assets, loader);
        assets.OpenAsset(scene);
        sceneTree.OpenAsset(scene);
        var root = new Node();
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
        var nodes = Enumerable.Range(0, 10).Select(index =>
        {
            var node = new Node { Parent = root, Name = $"Light {index}" };
            root.Children.Add(node);
            node.AddComponent(new LightComponent { Intensity = index + 1 });
            return node;
        }).ToArray();
        selection.SelectMany(nodes);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns("");
        var gui = new Gui { Input = input };
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!),
            null!, null!, new InspectorSettings(), null!, null!, undo, null!, null!, null!, null!);
        panel.Locked = locked;
        if (locked) selection.Select(root);
        using var surface = SKSurface.Create(new SKImageInfo(640, 1200));
        var font = Font.FromFamilyName("sans-serif", 14);
        void RenderFrame()
        {
            Frame(gui, surface, font, panel.Render);
            undo.Flush();
        }
        RenderFrame();
        var fields = InspectorForms.BuildForNodes(nodes).Sections[1].BodyFields;
        var index = fields.ToList().FindIndex(field => field.Name == nameof(LightComponent.Intensity));
        var editor = Descendants(gui.RootNode!).Single(node => node.Id == $"inspector/section1/field{index}/editor");
        input.MousePosition.Returns(new Vector2(editor.Rect.X + editor.Rect.W / 2, editor.Rect.Y + editor.Rect.H / 2));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        RenderFrame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        RenderFrame();
        input.GetTypedCharacters().Returns("1");
        RenderFrame();
        input.GetTypedCharacters().Returns("");
        input.IsKeyPressed(KeyboardKey.Enter).Returns(true);
        RenderFrame();
        input.IsKeyPressed(KeyboardKey.Enter).Returns(false);
        Assert.All(nodes, node => Assert.Equal(1f, node.GetComponent<LightComponent>()!.Intensity));
        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        for (var i = 0; i < nodes.Length; i++) Assert.Equal(i + 1, nodes[i].GetComponent<LightComponent>()!.Intensity);
        undo.Redo();
        Assert.All(nodes, node => Assert.Equal(1f, node.GetComponent<LightComponent>()!.Intensity));
        typeof(InspectorPanel).GetMethod("AddComponent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [typeof(ModelComponent)]);
        Assert.All(nodes, node => Assert.NotNull(node.GetComponent<ModelComponent>()));
        var representative = nodes[0].GetComponent<LightComponent>()!;
        typeof(InspectorPanel).GetMethod("MoveComponent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [representative, false]);
        Assert.All(nodes, node => Assert.IsType<LightComponent>(node.Components[1]));
        undo.Undo();
        Assert.All(nodes, node => Assert.IsType<LightComponent>(node.Components[0]));
        typeof(InspectorPanel).GetMethod("RemoveComponents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [representative]);
        Assert.All(nodes, node => Assert.Null(node.GetComponent<LightComponent>()));
    }

    /// <summary>A selection change between passes appears on the next frame without changing the form tree mid-frame.</summary>
    [Fact]
    public void MultiSelectionChangesOnlyOnNextCompleteFrame()
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var first = new Node();
        var second = new Node();
        first.AddComponent(new LightComponent());
        selection.Select(first);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!),
            null!, null!, new InspectorSettings(), null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame(gui, surface, font, current =>
        {
            if (current.Pass == Pass.Pass2Render) selection.SelectMany([first, second]);
            panel.Render(current);
        });
        Assert.Contains(Descendants(gui.RootNode!), node => node.Id == "inspector/section1");
        Frame(gui, surface, font, panel.Render);
        Assert.DoesNotContain(Descendants(gui.RootNode!), node => node.Id == "inspector/section1");
    }

    /// <summary>Invalid project slots and node references display warnings through both Inspector passes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayerValidationWarningsAreVisible(bool asset)
    {
        var settings = new TagsAndLayersSettings();
        settings.PhysicsLayers[3].Name = "";
        var project = new AppSettings();
        project.Loaded.Use(settings);
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        selection.Select(asset
            ? new AssetInspection(null!, "settings.dataasset", settings, "Settings", IsPayload: true)
            : new Node { PhysicsLayer = 3 });
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!),
            null!, null!, new InspectorSettings(), null!, null!, null!, null!, null!, null!, null!,
            new LayerFilter(project));
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame(gui, surface, font, panel.Render);
        Assert.Contains(Descendants(gui.RootNode!), node => node.Id == "inspector/layers/warning/0");
    }

    /// <summary>A settings selection made during rendering takes effect on the next complete frame.</summary>
    [Theory]
    [InlineData(typeof(PlayerSettings))]
    [InlineData(typeof(InputSettings))]
    [InlineData(typeof(GraphicsSettings))]
    public void SettingsSelectionBetweenPassesUsesNextFrame(Type settingsType)
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var picker = new ReferencePicker(null!, null!, null!);
        using var panel = new InspectorPanel(selection, assets, picker, null!, null!, new InspectorSettings(),
            null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        var inspection = new AssetInspection(null!, "settings.dataasset", Activator.CreateInstance(settingsType),
            "Settings", IsPayload: true);

        Frame(gui, surface, font, current =>
        {
            if (current.Pass == Pass.Pass2Render) selection.Select(inspection);
            panel.Render(current);
        });
        Assert.DoesNotContain(Descendants(gui.RootNode!), n => n.Id == "inspector/asset/fields");

        Frame(gui, surface, font, panel.Render);
        Assert.Contains(Descendants(gui.RootNode!), n => n.Id == "inspector/asset/fields");
    }

    /// <summary>Composed actions run only when enabled and clicked during the render pass.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedActionsRespectAvailability(bool enabled)
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var target = new ScopedRegistry { Name = "Studio" };
        var model = InspectorForms.Build(target, readOnly: true);
        var calls = 0;
        var section = model.Sections[0] with
        {
            Buttons = [new Button("Save", () =>
            {
                Assert.Equal(Pass.Pass2Render, gui.Pass);
                calls++;
            }, () => enabled)],
        };
        selection.Select(new FormInspection(target, model with { Sections = [section] }, "registry:studio"));
        using var panel = new InspectorPanel(selection, assets, null!, null!, null!, new InspectorSettings(),
            null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame(gui, surface, font, panel.Render);
        var button = Descendants(gui.RootNode!).Single(n => n.Id == "inspector/section0/button0");
        input.MousePosition.Returns(new Vector2(button.Rect.X + 5, button.Rect.Y + 5));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(enabled ? 1 : 0, calls);
    }

    /// <summary>
    /// A selected node draws through the shared renderer with Turian's drawers: its transform as the transform
    /// editor's vector rows, and each component as its own section with a reference slot for the model.
    /// </summary>
    [Fact]
    public void SelectedNodeDrawsTransformAndComponentThroughTurianDrawers()
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var node = new Node { Name = "probe" };
        node.Components.Add(new ModelComponent());
        selection.Select(node);
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!), null!, null!,
            new InspectorSettings(), null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);

        Frame(gui, surface, font, panel.Render);
        Frame(gui, surface, font, panel.Render);

        var ids = Descendants(gui.RootNode!).Select(n => n.Id).OfType<string>().ToList();
        Assert.Contains(ids, id => id.EndsWith("/pos", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith("/scale", StringComparison.Ordinal));
        Assert.Contains("inspector/section1", ids);
        Assert.Contains(ids, id => id.StartsWith("inspector/section1/", StringComparison.Ordinal)
                                   && id.EndsWith("/ref", StringComparison.Ordinal));
    }

    internal static void Frame(Gui gui, SKSurface surface, Font font, Action<Gui> render)
    {
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        render(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        render(gui);
        gui.Render();
        gui.EndFrame();
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
