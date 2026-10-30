namespace Turian.Tests;

/// <summary>Reference pickers remain reusable across successive designer wiring edits.</summary>
[Collection(SerialTests.Name)]
public sealed class ReferenceDrawerTests
{
    /// <summary>Selecting one reference closes its popup and the next field opens with one click.</summary>
    [Fact]
    public void PickerReopensForAnotherFieldAfterSelection()
    {
        var database = new AssetDatabase();
        var assets = new AssetManager();
        var loader = Substitute.For<IAssetLoader>();
        var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
            Substitute.For<ISceneManager>(), database);
        var root = new Node();
        var first = new Node { Name = "First", Parent = root };
        var second = new Node { Name = "Second", Parent = root };
        root.Children.Add(first);
        root.Children.Add(second);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tree, root);
        var target = new PickerFields();
        var fields = InspectorForms.Build(target).Sections[0].Fields;
        var drawer = new ReferenceDrawer(new ReferencePicker(database, tree, loader),
            new NodeInspectorController(assets), new AssetRevealService());
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns("");
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var font = Font.FromFamilyName("sans-serif", 14);
        void Draw() => InspectorFormsRenderingTests.Frame(gui, surface, font, current =>
        {
            using (current.Node().Expand().Direction(Axis.Vertical).Enter())
            {
                drawer.Draw(current, fields[0], "first", new FormRenderContext());
                drawer.Draw(current, fields[1], "second", new FormRenderContext());
            }
            drawer.DrawPendingPicker(current);
        });
        void Click(string id)
        {
            var node = Nodes(gui.RootNode!).Single(node => node.Id == id);
            input.MousePosition.Returns(node.Rect.Center);
            Draw();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            Draw();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
            Draw();
        }
        try
        {
            Draw();
            Click("first/ref/pick");
            Click("candidate/" + first.Id);
            Assert.Same(first, target.First);
            Click("second/ref/pick");
            Click("candidate/" + second.Id);
            Assert.Same(second, target.Second);
        }
        finally { font.Dispose(); }
    }

    static IEnumerable<LayoutNode> Nodes(LayoutNode root) => new[] { root }.Concat(root.Children.SelectMany(Nodes));

    sealed class PickerFields
    {
        /// <summary>The first reference edited in the same inspector.</summary>
        public Node? First { get; set; }
        /// <summary>The next reference whose picker must reopen independently.</summary>
        public Node? Second { get; set; }
    }
}
