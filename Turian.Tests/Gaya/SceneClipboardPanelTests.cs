namespace Turian.Tests;

/// <summary>Drives the scene tree and inspector clipboard actions through their panels.</summary>
[Collection(SerialTests.Name)]
public sealed class SceneClipboardPanelTests
{
    readonly MemoryClipboard clipboard = new();
    readonly AssetManager assets = new();
    readonly AssetDatabase database = new();
    readonly SettingsService settings = new();
    readonly IAssetLoader loader = Substitute.For<IAssetLoader>();
    readonly IInputHandler input = Substitute.For<IInputHandler>();
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(640, 900));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);
    readonly Node root = new() { Name = "Root" };
    readonly Prefab scene = new() { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
    readonly Gui gui;

    /// <summary>Builds a GUI whose clipboard the panels publish through the platform.</summary>
    public SceneClipboardPanelTests()
    {
        input.MousePosition.Returns(new Vector2(-1));
        gui = new Gui { Input = input };
        gui.Platform.Register<IClipboard>(clipboard);
        assets.OpenAsset(scene);
    }

    /// <summary>Copy, cut and paste move nodes through the system clipboard as undoable steps.</summary>
    [Fact]
    public void SceneTreeClipboardCopiesCutsAndPastes()
    {
        var tree = new SceneTreeController(assets, settings, null!, loader, Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        tree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        var child = new Node { Name = "Child", Parent = root };
        root.Children.Add(child);
        var panel = new SceneTreePanel(tree, inspector, assets, null!, null!, settings, undo, null!, null!, null!,
            database);
        var state = (TreeViewState)typeof(SceneTreePanel)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        state.SetExpanded("0", true);
        Frame(panel);

        inspector.Select(child);
        panel.CopySelected();
        Assert.True(NodeClipboard.HoldsNodes(clipboard.Text));

        // Opening the context menu builds every clipboard item, including Paste.
        RightClickRow(panel, 1);
        Frame(panel);

        panel.PasteSelected();
        Assert.Equal(2, root.Children.Count);

        inspector.Select(child);
        panel.CutSelected();
        Assert.Single(root.Children);
        Assert.True(NodeClipboard.HoldsNodes(clipboard.Text));

        var paste = typeof(SceneTreePanel).GetMethod("Paste", BindingFlags.Instance | BindingFlags.NonPublic)!;
        paste.Invoke(panel, [root, true]);
        Assert.Equal(2, root.Children.Count);
        Assert.Same(root, root.Children[1].Parent);

        clipboard.Text = "not a node";
        Assert.False(panel.CanPaste);
        panel.PasteSelected();
        inspector.ClearSelection();
        panel.CopySelected();
    }

    /// <summary>The component menu copies values and pastes them onto another component or as a new one.</summary>
    [Fact]
    public void InspectorComponentClipboardCopiesAndPastes()
    {
        var tree = new SceneTreeController(assets, settings, null!, loader, Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        tree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        var node = new Node { Name = "Node", Parent = root };
        root.Children.Add(node);
        var first = new ClipboardComponent { Value = 2f };
        var second = new ClipboardComponent { Value = 9f };
        node.AddComponent(first);
        node.AddComponent(second);
        inspector.Select(node);
        using var panel = new InspectorPanel(inspector, assets, new ReferencePicker(null!, null!, null!), null!,
            null!, new InspectorSettings(), null!, null!, undo, null!, null!, null!, database);
        Frame(panel);

        var copy = typeof(InspectorPanel).GetMethod("CopyComponent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        copy.Invoke(panel, [first]);
        Assert.True(NodeClipboard.HoldsComponent(clipboard.Text));

        var pasteValues = typeof(InspectorPanel)
            .GetMethod("PasteComponentValues", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pasteValues.Invoke(panel, [second, first]);
        Assert.Equal(first.Value, second.Value);

        // Right-clicking the component header builds the clipboard submenu with a matching component copied.
        var section = Descendants(gui.RootNode!).Single(static item => item.Id == "inspector/section1");
        input.MousePosition.Returns(section.Rect.Center);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(true);
        Frame(panel);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
        Frame(panel);

        var pasteNew = typeof(InspectorPanel)
            .GetMethod("PasteComponentAsNew", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pasteNew.Invoke(panel, [first]);
        Assert.Equal(3, node.Components.OfType<ClipboardComponent>().Count());
    }

    /// <summary>The context menu's clipboard and move items run the panel's actions on the selection.</summary>
    [Fact]
    public void ContextMenuItemsActOnTheSelection()
    {
        var tree = new SceneTreeController(assets, settings, null!, loader, Substitute.For<ISceneManager>(), database);
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(tree, inspector, assets, loader);
        tree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        var first = new Node { Name = "First", Parent = root };
        var second = new Node { Name = "Second", Parent = root };
        root.Children.Add(first);
        root.Children.Add(second);
        var panel = new SceneTreePanel(tree, inspector, assets, null!, null!, settings, undo, null!, null!, null!,
            database);
        Frame(panel);
        inspector.Select(first);

        var clipboardItems = Build(panel, "BuildClipboardMenu", first);
        Assert.Equal(["Cut", "Copy", "Paste", "Paste as Child"], Text(clipboardItems));
        var copy = clipboardItems.Single(static item => item.Text == "Copy");
        Assert.True(copy.Enabled);
        copy.Action!();
        Assert.True(NodeClipboard.HoldsNodes(clipboard.Text));

        var moveItems = Build(panel, "BuildMoveMenu", first);
        Assert.Equal(["Move Up", "Move Down"], Text(moveItems));
        Assert.False(moveItems[0].Enabled);
        moveItems[1].Action!();
        Assert.Same(first, root.Children[1]);
    }

    static List<FlyoutItem> Build(SceneTreePanel panel, string builder, Node node)
    {
        var menu = new FlyoutBuilder();
        typeof(SceneTreePanel).GetMethod(builder, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [menu, node]);
        return (List<FlyoutItem>)typeof(FlyoutBuilder).GetField("Items", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(menu)!;
    }

    static IEnumerable<string> Text(List<FlyoutItem> items) =>
        items.Select(static item => item.Text).Where(static text => text.Length > 0);

    void Frame(SceneTreePanel panel) => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

    void Frame(InspectorPanel panel) => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

    void RightClickRow(SceneTreePanel panel, int index)
    {
        var row = Descendants(gui.RootNode!).Single(item => item.Id == $"treeview/row{index}");
        input.MousePosition.Returns(row.Rect.Center);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(true);
        Frame(panel);
        input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
        Frame(panel);
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));

    sealed class MemoryClipboard : IClipboard
    {
        public string Text { get; set; } = string.Empty;
        public string GetClipboardText() => Text;
        public void SetClipboardText(string text) => Text = text;
    }

    [TypeId("c1b0f2a0-0000-4000-8000-000000000002")]
    sealed class ClipboardComponent : Component
    {
        public float Value { get; set; }
    }
}
