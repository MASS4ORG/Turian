namespace Turian.Tests;

/// <summary>Runs browser input on a disposable project with isolated Gaya preferences.</summary>
sealed class AssetBrowserHarness : IDisposable
{
    internal readonly string Project = Path.Combine(Path.GetTempPath(), $"turian-browser-{Guid.NewGuid():N}");
    internal string Root => Path.Combine(Project, "Assets");
    internal readonly IInputHandler Input = Substitute.For<IInputHandler>();
    internal readonly AssetBrowserSettings Preferences = new();
    internal readonly AssetBrowserPanel Panel;
    internal readonly AssetImporter Importer;
    internal readonly AssetDatabase Database = new();
    internal readonly UndoService Undo;
    internal readonly AssetLabelService Labels;
    internal readonly Gui Gui;
    internal readonly IClipboard Clipboard = Substitute.For<IClipboard>();
    internal readonly AssetRevealService Reveal = new();
    internal readonly FileDialogChrome Dialog = new();
    internal readonly NodeInspectorController Inspector;
    readonly BuildManager build;
    readonly SKSurface surface;
    readonly Font font = Font.FromFamilyName("sans-serif", 14);

    internal AssetBrowserHarness(AssetBrowserViewMode mode = AssetBrowserViewMode.Grid, int width = 800, int height = 500)
    {
        Directory.CreateDirectory(Path.Combine(Root, "Folder"));
        Directory.CreateDirectory(Path.Combine(Root, "Other"));
        File.WriteAllText(Path.Combine(Root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(Root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(Root, "Folder", "deep.txt"), "deep");
        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = Project };
        settings.Set(app);
        build = new BuildManager(app, NullLogger.Instance);
        Importer = new AssetImporter(NullLogger.Instance, Database, settings, build);
        Importer.StopMonitoring();
        var assets = new AssetManager();
        var loader = Substitute.For<IAssetLoader>();
        Inspector = new NodeInspectorController(assets);
        var tree = new SceneTreeController(assets, settings, Importer, loader, Substitute.For<ISceneManager>(), Database);
        Undo = new UndoService(tree, Inspector, assets, loader);
        Labels = new AssetLabelService(Importer, Undo);
        var files = new AssetFileSystem(settings, Importer);
        var types = new AssetTypeCatalog();
        var inspections = new AssetInspectionService(Importer, types, settings, loader, NullLogger.Instance);
        var workspace = new AssetWorkspace(assets, settings);
        var opener = new AssetOpenService(types, workspace, inspections, Inspector, NullLogger.Instance);
        Preferences.ViewMode = mode;
        Panel = new AssetBrowserPanel(files, settings, opener, inspections, Inspector, Reveal,
            new AssetCreationCatalog(files, settings, build, NullLogger.Instance), Preferences,
            Substitute.For<IEditorSettings>(), types, new AssetPreviewCatalog(build), null!,
            new AssetFileOperations(files, Undo), new BricksController(null, null, null, NullLogger.Instance),
            new ConfirmDialogChrome(new StudioLocalization()), build, importer: Importer, labelService: Labels,
            database: Database, fileDialogs: Dialog);
        Input.MousePosition.Returns(new Vector2(-1));
        Input.GetTypedCharacters().Returns("");
        Gui = new Gui { Input = Input };
        Gui.Platform.Register(Clipboard);
        surface = SKSurface.Create(new SKImageInfo(width, height));
        Frame();
        Frame();
    }

    internal TreeViewState State => Field<TreeViewState>("state");
    internal IReadOnlyList<AssetEntry> Entries => Field<IReadOnlyList<AssetEntry>>("entries");
    internal AssetEntry Entry(string name) => Entries.Single(entry => entry.AbsolutePath == Path.Combine(Root, name));
    internal T Field<T>(string name) => (T)typeof(AssetBrowserPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(Panel)!;
    internal void Set(string name, object value) => typeof(AssetBrowserPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(Panel, value);
    internal object? Invoke(string name, params object?[] args) => typeof(AssetBrowserPanel)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Panel, args);
    internal IEnumerable<LayoutNode> Nodes() => Descendants(Gui.RootNode!);
    internal LayoutNode Find(string id) => Nodes().Single(node => node.Id == id);

    internal void Frame() => InspectorFormsRenderingTests.Frame(Gui, surface, font, Panel.Render);
    internal void Frame(Action<Gui> render) => InspectorFormsRenderingTests.Frame(Gui, surface, font, render);

    internal void Click(string id, GMouseButton button = GMouseButton.Left)
    {
        Input.MousePosition.Returns(Find(id).Rect.Center);
        Frame();
        Input.IsMouseButtonPressed(button).Returns(true);
        Input.IsMouseButtonDown(button).Returns(true);
        Frame();
        Input.IsMouseButtonPressed(button).Returns(false);
        Input.IsMouseButtonDown(button).Returns(false);
        Frame();
    }

    internal void Search(string text)
    {
        Panel.FocusSearch();
        Frame();
        Input.IsKeyDown(GKey.LeftControl).Returns(true);
        Input.IsKeyPressed(GKey.A).Returns(true);
        Frame();
        Input.IsKeyDown(GKey.LeftControl).Returns(false);
        Input.IsKeyPressed(GKey.A).Returns(false);
        Input.GetTypedCharacters().Returns(text);
        Frame();
        Input.GetTypedCharacters().Returns("");
        Frame();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Panel.Dispose();
        Importer.Dispose();
        Undo.Dispose();
        build.Dispose();
        font.Dispose();
        surface.Dispose();
        Directory.Delete(Project, recursive: true);
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
