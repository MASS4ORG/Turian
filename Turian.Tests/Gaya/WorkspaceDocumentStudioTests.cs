namespace Turian.Tests;

/// <summary>Contributed documents in the Studio: their layout, tabs, close prompt, opening and Undo/Redo.</summary>
[Collection(SerialTests.Name)]
public sealed class WorkspaceDocumentStudioTests : IDisposable
{
    sealed class BlankPanel : IPanel
    {
        public void Render(Gui gui)
        {
        }
    }

    sealed class TrackingPanel : IPanel, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Render(Gui gui)
        {
        }

        public void Dispose() => Disposed = true;
    }

    const string FakePanelId = "test.fake.panel";
    const string ScenePanelId = "test.scene.panel";

    readonly DocumentProject project = new();

    /// <inheritdoc />
    public void Dispose() => project.Dispose();

    static void RegisterKinds(GayaApplication app)
    {
        app.DocumentKinds.Register(SceneDocument.SceneKind);
        app.DocumentKinds.Register(new DocumentKind(FakeDocumentKind.KindId, "Fake"));
        app.Panels.Register(PanelDescriptor.ForDocuments(ScenePanelId, "Scene Probe", PanelPlacement.Center,
            [SceneDocument.KindId], static (_, _) => new BlankPanel()));
        app.Panels.Register(PanelDescriptor.ForDocuments(FakePanelId, "Fake Probe", PanelPlacement.Center,
            [FakeDocumentKind.KindId], static (_, _) => new BlankPanel()));
    }

    /// <summary>The bridge publishes a contributed tab with its kind and title, and the workbench swaps layouts.</summary>
    [Fact]
    public void ContributedTabsBecomeDocumentsWithTheirOwnLayout()
    {
        using var frame = new WorkbenchFrame(configure: RegisterKinds);
        using var bridge = new DocumentWorkspaceBridge(project.Workspace, frame.Documents);
        var scene = project.Workspace.Open(project.Source("level.prefab").Path)!;
        frame.Draw();
        Assert.Equal([ScenePanelId], frame.Workbench.Layout.PanelIds);

        var (menu, document) = project.OpenFake("menu");
        frame.Draw();

        var published = Assert.IsType<ContributedDocument>(frame.Documents.Active);
        Assert.Equal((menu.Id, FakeDocumentKind.KindId, "menu"), (published.Id, published.Kind, published.Title));
        Assert.Same(menu, published.Wrapper);
        Assert.Equal([FakePanelId], frame.Workbench.Layout.PanelIds);

        project.Workspace.Activate(scene);
        frame.Draw();
        Assert.Equal([ScenePanelId], frame.Workbench.Layout.PanelIds);

        project.Workspace.Close(menu);
        Assert.True(document.Disposed);
        Assert.Equal([scene.Id], frame.Documents.Documents.Select(static open => open.Id));
    }

    /// <summary>Undo and Redo act on the active document, and a scene tab still reaches the project's history.</summary>
    [Fact]
    public void UndoAndRedoRouteToTheActiveDocument()
    {
        var loader = Substitute.For<IAssetLoader>();
        var inspector = new NodeInspectorController(project.Assets);
        var tree = new SceneTreeController(project.Assets, project.Settings, null!, loader,
            Substitute.For<ISceneManager>(), new AssetDatabase());
        using var undo = new UndoService(tree, inspector, project.Assets, loader);
        project.Undo = undo;
        using var services = new ServiceCollection().AddSingleton(project.Workspace)
            .AddSingleton(undo).AddSingleton(new StudioLocalization()).BuildServiceProvider();
        var context = Substitute.For<IPluginContext>();
        StudioCommands.Register(context);
        CommandDescriptor Command(string id) => context.Commands.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(ICommandRegistry.Register))
            .Select(static call => (CommandDescriptor)call.GetArguments()[0]!).Last(command => command.Id == id);
        var back = Command("gaya.turian.undo");
        var forth = Command("gaya.turian.redo");

        var scene = project.Workspace.Open(project.Source("level.prefab").Path)!;
        var renamed = "after";
        scene.History.Record("Rename", () => renamed = "before", () => renamed = "after");
        var (menu, document) = project.OpenFake("menu");
        Assert.False(back.CanExecute!(services));
        document.Edit();

        Assert.True(back.CanExecute!(services));
        Assert.Equal("Undo Edit Value", back.MenuLabelFor(services));
        back.Execute(services);
        Assert.Equal(0, document.Value);
        Assert.Equal("after", renamed);
        Assert.True(forth.CanExecute!(services));
        Assert.Equal("Redo Edit Value", forth.MenuLabelFor(services));
        forth.Execute(services);
        Assert.Equal(1, document.Value);

        project.Workspace.Activate(scene);
        Assert.Equal("Undo Rename", back.MenuLabelFor(services));
        back.Execute(services);
        Assert.Equal("before", renamed);
        Assert.Equal("Rename", undo.RedoLabel);
        Assert.Equal(1, document.Value);
        forth.Execute(services);
        Assert.Equal("after", renamed);
        Assert.True(menu.History.CanUndo);

        // With no tab open, project-wide steps stay reachable.
        project.Workspace.Close(menu);
        project.Workspace.Close(scene);
        undo.Perform("Move File", [], static () => { }, () => renamed = "moved back",
            document: UndoService.ProjectDocument);
        Assert.Equal("Undo Move File", back.MenuLabelFor(services));
        back.Execute(services);
        Assert.Equal("moved back", renamed);
    }

    /// <summary>A tab shows its warning before its lock, and neither when the document is fine.</summary>
    [Fact]
    public void TabsMarkConflictsAndReadOnlySources()
    {
        var (plain, _) = project.OpenFake("plain");
        var (locked, document) = project.OpenFake("locked", readOnly: true);

        Assert.Empty(DocumentTabsChrome.Markers(plain));
        Assert.Equal([Icons.Lock], DocumentTabsChrome.Markers(locked));
        document.ConflictWith("changed on disk");
        Assert.Equal([Icons.LogWarning, Icons.Lock], DocumentTabsChrome.Markers(locked));
    }

    /// <summary>The strip draws every kind of tab; picking one activates it and middle-clicking one closes it.</summary>
    [Fact]
    public void TheStripDrawsActivatesAndClosesTabs()
    {
        using var browser = new AssetBrowserHarness();
        var (menu, document) = project.OpenFake("menu", readOnly: true);
        var scene = project.Workspace.Open(project.Source("level.prefab").Path)!;
        document.Edit();
        document.ConflictWith("changed on disk");
        var dialog = new UnsavedChangesDialogChrome(new StudioLocalization());
        var chrome = new DocumentTabsChrome(project.Workspace, dialog, new StudioLocalization());
        browser.Frame(chrome.Render);
        browser.Frame(chrome.Render);
        Assert.Same(scene, project.Workspace.Active);

        void Press(AssetWrapper tab, GMouseButton button)
        {
            var node = browser.Nodes().First(node => node.Rect.W > 0 && node.Rect.H > 0
                && node.Id.Contains(tab.Id, StringComparison.Ordinal));
            browser.Input.MousePosition.Returns(node.Rect.Center);
            browser.Frame(chrome.Render);
            browser.Input.IsMouseButtonPressed(button).Returns(true);
            browser.Input.IsMouseButtonDown(button).Returns(true);
            browser.Frame(chrome.Render);
            browser.Input.IsMouseButtonPressed(button).Returns(false);
            browser.Input.IsMouseButtonDown(button).Returns(false);
            browser.Frame(chrome.Render);
        }

        Press(menu, GMouseButton.Left);
        Assert.Same(menu, project.Workspace.Active);

        Press(scene, GMouseButton.Middle);
        Assert.Equal([menu], project.Workspace.Documents);
    }

    /// <summary>Closing a dirty tab asks first, and choosing Save keeps it open when the write fails.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ClosingADirtyTabAsksAndKeepsItWhenTheSaveFails(int button, bool kept)
    {
        using var browser = new AssetBrowserHarness();
        var (menu, document) = project.OpenFake("menu");
        document.Edit();
        document.SaveFailure = "locked";
        var dialog = new UnsavedChangesDialogChrome(new StudioLocalization());
        var chrome = new DocumentTabsChrome(project.Workspace, dialog, new StudioLocalization());

        Close(chrome, menu);
        Assert.Single(project.Workspace.Documents);
        for (var settle = 0; settle < 3; settle++)
        {
            browser.Frame(dialog.Render);
        }

        var footer = browser.Nodes().Single(static node => node.Children.Count == 3
            && node.Id.Contains(nameof(UnsavedChangesDialogChrome), StringComparison.Ordinal));
        browser.Input.MousePosition.Returns(footer.Children[button].Rect.Center);
        browser.Frame(dialog.Render);
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        browser.Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        browser.Frame(dialog.Render);
        browser.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        browser.Input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        browser.Frame(dialog.Render);

        Assert.Equal(kept ? 1 : 0, project.Workspace.Documents.Count);
        Assert.Equal(!kept, document.Disposed);
        Assert.Equal(button == 0 ? 1 : 0, document.Saves.Count);
    }

    /// <summary>Closing a clean tab needs no question.</summary>
    [Fact]
    public void ClosingACleanTabClosesIt()
    {
        var (menu, document) = project.OpenFake("menu");
        var chrome = new DocumentTabsChrome(project.Workspace,
            new UnsavedChangesDialogChrome(new StudioLocalization()), new StudioLocalization());

        Close(chrome, menu);

        Assert.True(document.Disposed);
    }

    /// <summary>Closing a contributed tab disposes its document and the panel instances Gaya built for it.</summary>
    [Fact]
    public void ClosingAContributedTabReleasesItsPanels()
    {
        var panels = new List<TrackingPanel>();
        using var frame = new WorkbenchFrame(configure: app => RegisterTrackedPanels(app, panels));
        using var bridge = new DocumentWorkspaceBridge(project.Workspace, frame.Documents);
        var (menu, document) = project.OpenFake("menu");
        frame.Draw();
        var panel = Assert.Single(panels);
        var closed = new List<string>();
        frame.Documents.Closed += open => closed.Add(open.Id);

        project.Workspace.Close(menu);

        Assert.True(panel.Disposed);
        Assert.True(document.Disposed);
        Assert.Equal([menu.Id], closed);
        Assert.Empty(frame.Documents.Documents);
        Assert.Null(frame.Documents.Active);
    }

    /// <summary>Leaving the project disposes a dirty contributed document and releases its panels with it.</summary>
    [Fact]
    public void ProjectSwitchReleasesADirtyContributedDocumentAndItsPanels()
    {
        var panels = new List<TrackingPanel>();
        using var frame = new WorkbenchFrame(configure: app => RegisterTrackedPanels(app, panels));
        using var bridge = new DocumentWorkspaceBridge(project.Workspace, frame.Documents);
        var (_, document) = project.OpenFake("menu");
        frame.Draw();
        document.Edit();
        var panel = Assert.Single(panels);

        project.Settings.Set(new AppSettings());

        Assert.True(document.Disposed);
        Assert.True(panel.Disposed);
        Assert.Empty(project.Workspace.Documents);
        Assert.Empty(frame.Documents.Documents);
        Assert.Null(frame.Documents.Active);
    }

    /// <summary>Undo and its label follow the active tab both ways; a tab without steps offers none.</summary>
    [Fact]
    public void UndoFollowsTheActiveTabInBothDirections()
    {
        var loader = Substitute.For<IAssetLoader>();
        var inspector = new NodeInspectorController(project.Assets);
        var tree = new SceneTreeController(project.Assets, project.Settings, null!, loader,
            Substitute.For<ISceneManager>(), new AssetDatabase());
        using var undo = new UndoService(tree, inspector, project.Assets, loader);
        project.Undo = undo;
        using var services = new ServiceCollection().AddSingleton(project.Workspace)
            .AddSingleton(undo).AddSingleton(new StudioLocalization()).BuildServiceProvider();
        var context = Substitute.For<IPluginContext>();
        StudioCommands.Register(context);
        CommandDescriptor Command(string id) => context.Commands.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(ICommandRegistry.Register))
            .Select(static call => (CommandDescriptor)call.GetArguments()[0]!).Last(command => command.Id == id);
        var back = Command("gaya.turian.undo");
        var forth = Command("gaya.turian.redo");

        var scene = project.Workspace.Open(project.Source("level.prefab").Path)!;
        var renamed = "after";
        scene.History.Record("Rename", () => renamed = "before", () => renamed = "after");
        var (menu, document) = project.OpenFake("menu");
        document.Edit();

        project.Workspace.Activate(scene);
        Assert.True(back.CanExecute!(services));
        Assert.Equal("Undo Rename", back.MenuLabelFor(services));
        Assert.False(forth.CanExecute!(services));

        project.Workspace.Activate(menu);
        Assert.True(back.CanExecute!(services));
        Assert.Equal("Undo Edit Value", back.MenuLabelFor(services));
        back.Execute(services);
        Assert.Equal(0, document.Value);
        Assert.Equal("after", renamed);

        // The contributed tab has no steps left, although the scene still has one.
        Assert.False(back.CanExecute!(services));
        Assert.Equal("Redo Edit Value", forth.MenuLabelFor(services));

        project.Workspace.Activate(scene);
        Assert.True(back.CanExecute!(services));
        Assert.Equal("Undo Rename", back.MenuLabelFor(services));
        Assert.False(forth.CanExecute!(services));
    }

    static void RegisterTrackedPanels(GayaApplication app, List<TrackingPanel> panels)
    {
        app.DocumentKinds.Register(new DocumentKind(FakeDocumentKind.KindId, "Fake"));
        app.Panels.Register(PanelDescriptor.ForDocuments(FakePanelId, "Fake Probe", PanelPlacement.Center,
            [FakeDocumentKind.KindId], (_, _) =>
            {
                var panel = new TrackingPanel();
                panels.Add(panel);
                return panel;
            }));
    }

    /// <summary>The asset browser gives a claimed source to its kind, read-only inside an installed brick.</summary>
    [Fact]
    public void OpeningFromTheBrowserPrefersTheContributedKind()
    {
        using var browser = new AssetBrowserHarness();
        var package = Path.Combine(browser.Project, "package");
        typeof(AssetImporter).GetField("packageRoots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(browser.Importer, new List<(string Root, bool ReadOnly)> { (package, true) });
        var kind = new FakeDocumentKind();
        var registry = new WorkspaceDocumentKindRegistry([kind]);
        using var services = new ServiceCollection().AddSingleton(browser.Importer).AddSingleton(registry)
            .BuildServiceProvider();
        using var workspace = new AssetWorkspace(new AssetManager(), browser.Settings, services: services);
        var opener = new AssetOpenService(new AssetTypeCatalog([], registry), workspace, null!, browser.Inspector,
            null!, NullLogger.Instance, browser.Importer);
        var source = Write(Path.Combine(package, "screen.fake"));

        Assert.Equal(AssetActivation.Edit, opener.Open(source, null));
        Assert.True(Assert.Single(kind.Opened).Context.IsReadOnly);
        Assert.True(workspace.Active!.IsReadOnly);

        kind.OpenFailure = new InvalidDataException("corrupt");
        Assert.Equal(AssetActivation.None, opener.Open(Write(Path.Combine(browser.Root, "broken.fake")), null));
        Assert.Single(workspace.Documents);
    }

    static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        File.WriteAllText(path + ".meta",
            Serializer.Serialize(new Asset { Id = Guid.NewGuid(), RelativePath = path }));
        return path;
    }

    static void Close(DocumentTabsChrome chrome, AssetWrapper tab) =>
        typeof(DocumentTabsChrome).GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(chrome, [tab]);
}
