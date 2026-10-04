namespace Turian.Tests;

/// <summary>Runs Scene toolbar input on a headless GUI with a real camera controller.</summary>
sealed class SceneToolbarHarness : IDisposable
{
    readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(800, 500));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);
    readonly UndoService undo;
    internal readonly IInputHandler Input = Substitute.For<IInputHandler>();
    internal readonly EditorCameraSettings Settings = new() { Store = Substitute.For<IEditorSettings>() };
    internal readonly SceneViewport Viewport;
    internal readonly SceneToolbar Toolbar;
    internal readonly Gui Gui;

    internal SceneToolbarHarness()
    {
        var assets = new AssetManager();
        var database = new AssetDatabase();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        undo = new UndoService(tree, inspector, assets, loader);
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), database, services, NullLogger.Instance);
        Viewport = new SceneViewport(null!, database, tree, inspector, null!, play, Settings,
            NullLogger.Instance, undo, new LocaleService());
        Toolbar = new SceneToolbar(Viewport, () => { });
        Input.MousePosition.Returns(new Vector2(-1));
        Input.GetTypedCharacters().Returns(string.Empty);
        Gui = new Gui { Input = Input };
        Frame();
    }

    internal void Frame() => InspectorFormsRenderingTests.Frame(Gui, surface, font, current =>
    {
        using (current.Node(800, 500).Direction(Axis.Vertical).Enter()) Toolbar.Render(current);
    });

    internal IEnumerable<LayoutNode> Nodes() => Descendants(Gui.RootNode!);
    internal LayoutNode Find(string id) => Nodes().Single(node => node.Id == id);

    internal void Click(string id)
    {
        Input.MousePosition.Returns(Find(id).Rect.Center);
        Frame();
        Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame();
        Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        Frame();
    }

    internal void Replace(string id, string text)
    {
        Click("scene/options/value/" + id);
        Input.IsKeyDown(GKey.LeftControl).Returns(true);
        Input.IsKeyPressed(GKey.A).Returns(true);
        Frame();
        Input.IsKeyDown(GKey.LeftControl).Returns(false);
        Input.IsKeyPressed(GKey.A).Returns(false);
        Input.GetTypedCharacters().Returns(text);
        Frame();
        Input.GetTypedCharacters().Returns(string.Empty);
        Frame();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Viewport.Dispose();
        undo.Dispose();
        surface.Dispose();
        services.Dispose();
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
