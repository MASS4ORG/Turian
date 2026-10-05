namespace Turian.Tests;

/// <summary>Checks toolbar and popover input through both GUI passes without a window.</summary>
public sealed class SceneToolbarTests
{
    /// <summary>Tool buttons, menus and popovers perform their actions only in the input pass.</summary>
    [Fact]
    public void ToolbarGroupsWorkingToolsAndViewOptions()
    {
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        var settings = new EditorCameraSettings { Store = Substitute.For<IEditorSettings>() };
        var shortcuts = new ShortcutService(NullLogger.Instance);
        shortcuts.Add(new KeyBinding("gaya.turian.viewport.frameSelected", KeyboardKey.F));
        shortcuts.Add(new KeyBinding("gaya.turian.viewport.translate", KeyboardKey.W));
        settings.Navigation = new SceneNavigationBindings(shortcuts, Substitute.For<IFocusTracker>());
        using var viewport = new SceneViewport(null!, database, tree, inspector, null!, play, settings,
            NullLogger.Instance, undo, new LocaleService());
        var controller = new SceneCameraController(new EditorCamera());
        typeof(SceneViewport).GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewport, controller);
        var framed = 0;
        var toolbar = new SceneToolbar(viewport, () => framed++);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(800, 500));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        Assert.DoesNotContain(Nodes(), node => node.Id == "scene/toolbar/culling");
        Assert.True(Find("scene/toolbar/Select").Rect.X < Find("scene/toolbar/Translate").Rect.X);
        foreach (var mode in Enum.GetValues<TransformGizmoMode>())
        {
            Click("scene/toolbar/" + mode);
            Assert.Equal(mode, viewport.Gizmo.Mode);
        }
        Click("scene/toolbar/frame");
        Assert.Equal(1, framed);
        Click("scene/toolbar/projection");
        Assert.True(viewport.IsOrthographic);
        Click("scene/toolbar/space");
        Assert.Equal(TransformGizmoSpace.Local, viewport.Gizmo.Space);
        Click("scene/toolbar/snap");
        Assert.False(settings.Tools.SnapEnabled);
        Assert.Equal(1, settings.Tools.TranslationSnap);
        Click("scene/toolbar/snapOptions");
        Assert.NotNull(Find("scene/options/value/move"));
        Assert.NotNull(Find("scene/options/value/rotate"));
        Assert.NotNull(Find("scene/options/value/scale"));
        input.IsKeyPressed(KeyboardKey.Escape).Returns(true);
        Frame();
        input.IsKeyPressed(KeyboardKey.Escape).Returns(false);
        Frame();
        Assert.DoesNotContain(Nodes(), node => node.Id == "scene/options");
        Click("scene/toolbar/Transform");
        MenuClick(0);
        Assert.Equal(TransformGizmoSpace.World, viewport.Gizmo.Space);
        Click("scene/toolbar/Transform");
        MenuClick(1);
        Assert.True(settings.Tools.SnapEnabled);
        settings.Tools.TranslationSnap = 4;
        settings.Tools.RotationSnap = 35;
        settings.Tools.ScaleSnap = 0.7f;
        Click("scene/toolbar/Transform");
        MenuClick(4);
        Assert.Equal((1f, 15f, 0.1f),
            (settings.Tools.TranslationSnap, settings.Tools.RotationSnap, settings.Tools.ScaleSnap));
        Click("scene/toolbar/Transform");
        MenuClick(3);
        Assert.NotNull(Find("scene/options"));
        Click("scene/toolbar/closeOptions");
        Click("scene/toolbar/View");
        MenuClick(0);
        Assert.Equal(2, framed);
        Click("scene/toolbar/View");
        MenuClick(1);
        Assert.False(viewport.IsOrthographic);
        Click("scene/toolbar/View");
        MenuClick(3);
        Assert.False(settings.Grid.Visible);
        Click("scene/toolbar/View");
        MenuClick(4);
        Assert.False(settings.Gizmos.Visible);
        Click("scene/toolbar/View");
        MenuClick(5);
        Assert.False(settings.Gizmos.ShowOrientation);
        Click("scene/toolbar/View");
        MenuClick(6);
        SubmenuClick("Visible layers", 0);
        Assert.False(settings.View.VisibleLayers.Contains(0));
        Click("scene/toolbar/View");
        MenuClick(7);
        SubmenuClick("Locked layers", 0);
        Assert.True(settings.View.LockedLayers.Contains(0));
        Click("scene/toolbar/View");
        MenuClick(9);
        Assert.NotNull(Find("scene/options/value/fov"));
        Assert.NotNull(Find("scene/options/value/near"));
        Assert.NotNull(Find("scene/options/value/far"));
        Assert.NotNull(Find("scene/options/value/speed"));
        Assert.NotNull(Find("scene/options/value/look"));
        var options = Find("scene/options").Rect;
        var toolbarBounds = Find("scene/toolbar").Rect;
        Assert.True(options.X >= toolbarBounds.X && options.BottomRight.X <= toolbarBounds.BottomRight.X);
        Assert.Equal(toolbarBounds.BottomRight.Y + 2f, options.Y);
        ClickAt(new Vector2(5, 400));
        Assert.DoesNotContain(Nodes(), node => node.Id == "scene/options");
        settings.Store.Received().NotifyChanged("gaya.turian.sceneGrid");
        settings.Store.Received().NotifyChanged("gaya.turian.sceneGizmos");
        settings.Store.Received().NotifyChanged("gaya.turian.sceneTransform");
        var position = controller.Camera.Position;
        viewport.MoveCamera(-1);
        Assert.Equal(position, controller.Camera.Position);
        viewport.MoveCamera(0);
        Assert.True(Vector3.Dot(controller.Camera.Position - position, controller.Camera.Front) > 0);

        void MenuClick(int index) => Click(Nodes().Single(node =>
            node.Id.Contains("/menubar/", StringComparison.Ordinal) && node.Id.EndsWith("/i" + index)).Id);
        void SubmenuClick(string name, int index)
        {
            Click(Nodes().Single(node => node.Id.Contains($"/{name}/", StringComparison.Ordinal)
                && node.Id.EndsWith("/i" + index)).Id);
        }
        void Click(string id) => ClickAt(Find(id).Rect.Center);
        void ClickAt(Vector2 point)
        {
            input.MousePosition.Returns(point);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Frame();
        }
        LayoutNode Find(string id) => Nodes().Single(node => node.Id == id);
        IEnumerable<LayoutNode> Nodes() => Descendants(gui.RootNode!);
        void Frame()
        {
            var mode = viewport.Gizmo.Mode;
            var space = viewport.Gizmo.Space;
            var snap = settings.Tools.SnapEnabled;
            InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
            {
                using (current.Node(800, 500).Direction(Axis.Vertical).Enter()) toolbar.Render(current);
                if (current.Pass != Pass.Pass1Build) return;
                Assert.Equal(mode, viewport.Gizmo.Mode);
                Assert.Equal(space, viewport.Gizmo.Space);
                Assert.Equal(snap, settings.Tools.SnapEnabled);
            });
        }
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
