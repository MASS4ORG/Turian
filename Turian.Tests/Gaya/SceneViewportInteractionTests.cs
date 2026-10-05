namespace Turian.Tests;

/// <summary>Exercises transform gestures and their feedback through the actual two-pass scene viewport.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneViewportInteractionTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Hover and rotation feedback survive both GUI passes, and a drag becomes one undo operation.</summary>
    [Fact]
    public void RotationGestureShowsFeedbackAndRecordsUndo()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        var scene = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
        assets.OpenAsset(scene);
        tree.OpenAsset(scene);
        var root = new Node();
        var node = new Node { Position = new Vector3(0, 1, 0), Parent = root };
        root.Children.Add(node);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var viewport = new SceneViewport(fixture.Vulkan, database, tree, inspector, new GizmoDrawerCatalog(build), play,
            new EditorCameraSettings(), NullLogger.Instance, undo, new LocaleService());
        inspector.Select(node);
        viewport.Gizmo.Mode = TransformGizmoMode.Rotate;
        viewport.Gizmo.SnapRotation = 0;
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(960, 540));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        Assert.Null(typeof(SceneViewport).GetField("failure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewport));

        input.MousePosition.Returns(new Vector2(419, 224));
        Frame();
        Assert.Equal(TransformGizmoAxis.Z, viewport.Gizmo.Axis);
        Assert.True(CursorVisible());
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        Assert.True(viewport.Gizmo.IsDragging);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.MousePosition.Returns(new Vector2(450, 200));
        Frame();
        Assert.NotEqual(0, viewport.Gizmo.RotationDegrees);
        Assert.NotEqual(Quaternion.Identity, node.Orientation);
        Assert.Contains(Descendants(gui.RootNode!), child => child.Id == "scene/rotationFeedback");
        Assert.Contains(Descendants(gui.RootNode!), child => child.Id == "scene/gizmoCursor");
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame();
        Assert.False(viewport.Gizmo.IsDragging);
        Assert.True(undo.CanUndo);
        undo.Undo();
        Assert.Equal(Quaternion.Identity, node.Orientation);
        Assert.False(undo.CanUndo);

        input.MousePosition.Returns(new Vector2(-1));
        Frame();
        Assert.Equal(TransformGizmoAxis.None, viewport.Gizmo.Axis);
        Assert.False(CursorVisible());
        input.MousePosition.Returns(new Vector2(800, 400));
        input.MouseWheelDelta.Returns(1f);
        Frame();
        input.MouseWheelDelta.Returns(0f);
        var orientation = Descendants(gui.RootNode!).Single(child => child.Id == "scene/orientation");
        input.MousePosition.Returns(orientation.Rect.Center);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        Assert.Same(node, inspector.SelectedNode);
        Assert.False(viewport.Gizmo.IsDragging);
        var viewer = (SceneViewerService)typeof(SceneViewport)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
        Assert.True(viewer.Camera.IsOrthographic);
        Assert.Equal(new Vector4(viewport.Settings.View.EmptySkyColor, 1f), viewer.ClearColor);
        viewport.Settings.View.EmptySkyColor = new Vector3(0.1f, 0.2f, 0.3f);
        Frame();
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), viewer.ClearColor);

        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        using var workspace = new AssetWorkspace(assets, new SettingsService());
        using var stage = new PrefabStage(workspace, database);
        var panel = new ScenePanel(viewport, tree, inspector, workspace, stage);
        PanelFrame();
        input.MousePosition.Returns(Descendants(gui.RootNode!).Single(child => child.Id == "scene/toolbar/frame").Rect.Center);
        PanelFrame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        PanelFrame();
        Assert.True(viewer.Camera.Project(node.GlobalTransform.Position).Length() < 1e-4f);
        var cameraPosition = viewer.Camera.Position;
        panel.MoveCamera(0);
        Assert.NotEqual(cameraPosition, viewer.Camera.Position);
        var commands = new List<CommandDescriptor>();
        var context = Substitute.For<IPluginContext>();
        context.Commands.When(registry => registry.Register(Arg.Any<CommandDescriptor>()))
            .Do(call => commands.Add(call.Arg<CommandDescriptor>()));
        SceneNavigationBindings.Register(context);
        var accessor = Substitute.For<IPanelAccessor>();
        accessor.Panel(GayaPlugin.ViewportPanelId).Returns(panel);
        using var commandServices = new ServiceCollection().AddSingleton(accessor).BuildServiceProvider();
        commands.Single(command => command.Id == SceneNavigationBindings.CommandId(1)).Execute(commandServices);
        Assert.True(Vector3.Distance(cameraPosition, viewer.Camera.Position) < 1e-5f);
        commands[0].Execute(services);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        viewport.Settings.Grid.Visible = false;
        node.AddComponent(new LightComponent());
        PanelFrame();
        Assert.NotEmpty(viewer.Gizmos.WorldLines);
        viewport.Settings.Gizmos.Visible = false;
        PanelFrame();
        Assert.Empty(viewer.Gizmos.WorldLines);
        Assert.Empty(viewer.Gizmos.OverlayTriangles);

        viewport.Gizmo.Mode = TransformGizmoMode.Select;
        inspector.Select(node);
        var initialPosition = viewer.Camera.Position;
        void DragRectangle()
        {
            input.MousePosition.Returns(new Vector2(500, 300));
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            input.MousePosition.Returns(new Vector2(600, 400));
            Frame();
            input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
            Frame();
        }
        DragRectangle();
        Assert.Empty(inspector.SelectedNodes);
        Assert.Equal(initialPosition, viewer.Camera.Position);
        inspector.Select(node);
        input.IsKeyDown(KeyboardKey.LeftShift).Returns(true);
        DragRectangle();
        Assert.Equal([node], inspector.SelectedNodes);
        input.IsKeyDown(KeyboardKey.LeftShift).Returns(false);
        input.IsKeyDown(KeyboardKey.LeftControl).Returns(true);
        DragRectangle();
        Assert.Equal([node], inspector.SelectedNodes);
        input.MousePosition.Returns(new Vector2(500, 300));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame();
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame();
        Assert.Equal([node], inspector.SelectedNodes);
        input.IsKeyDown(KeyboardKey.LeftControl).Returns(false);

        void PanelFrame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        bool CursorVisible() => (bool)typeof(SceneViewport)
            .GetField("showGizmoCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;

        void Frame()
        {
            InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
            {
                using (current.Node(960, 540, "viewport").Enter()) viewport.Render(current);
            });
            undo.Flush();
        }
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
