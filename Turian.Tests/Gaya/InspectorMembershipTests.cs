namespace Turian.Tests;

/// <summary>Checks searchable tag and mask edits across owners, GUI passes and locked selections.</summary>
[Collection(SerialTests.Name)]
public sealed class InspectorMembershipTests
{
    sealed class Harness : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(640, 900));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly InspectorPanel panel;
        internal readonly Node First = new() { Name = "First", Tags = ["A", "Unknown first"] };
        internal readonly Node Second = new() { Name = "Second", Tags = ["B", "Unknown second"] };
        internal readonly UndoService Undo;
        internal readonly Gui Gui;
        internal readonly IInputHandler Input = Substitute.For<IInputHandler>();

        internal Harness(bool locked)
        {
            var assets = new AssetManager();
            var loader = Substitute.For<IAssetLoader>();
            var database = new AssetDatabase();
            var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
                Substitute.For<ISceneManager>(), database);
            var root = new Node { Children = { First, Second } };
            root.Awake(null);
            First.AddComponent(new LightComponent { CullingMask = LayerMask.FromLayer(0) | LayerMask.FromLayer(7) });
            Second.AddComponent(new LightComponent { CullingMask = LayerMask.FromLayer(1) | LayerMask.FromLayer(9) });
            var selection = new NodeInspectorController(assets);
            Undo = new UndoService(tree, selection, assets, loader);
            var scene = new Prefab { Id = Guid.NewGuid(), RelativePath = "Assets/test.prefab" };
            assets.OpenAsset(scene);
            tree.OpenAsset(scene);
            typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tree, root);
            selection.SelectMany([First, Second]);
            var project = new AppSettings();
            project.Loaded.Use(new TagsAndLayersSettings
            {
                Tags = ["A", "B", "Shared", "Untagged"],
                RenderLayers = [new LayerSlot { Index = 0, Name = "Default" },
                    new LayerSlot { Index = 1, Name = "Other" }, new LayerSlot { Index = 31, Name = "High" }],
            });
            panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!),
                null!, null!, new InspectorSettings(), null!, null!, Undo, null!, null!, null!, database,
                new LayerFilter(project));
            panel.Locked = locked;
            if (locked) selection.Select(root);
            Input.MousePosition.Returns(new Vector2(-1));
            Input.GetTypedCharacters().Returns("");
            Gui = new Gui { Input = Input };
            Frame();
        }

        internal void Frame()
        {
            InspectorFormsRenderingTests.Frame(Gui, surface, font, panel.Render);
            Undo.Flush();
        }

        internal IEnumerable<LayoutNode> Nodes => Descendants(Gui.RootNode!);
        static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Descendants));

        internal LayoutNode Find(string widget, string suffix) => Nodes.Single(node =>
            node.Id.Contains($"/{widget}:", StringComparison.Ordinal)
            && node.Id.EndsWith(suffix, StringComparison.Ordinal));

        internal void Click(LayoutNode node)
        {
            Input.MousePosition.Returns(node.Rect.Center);
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Frame();
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Frame();
        }

        internal void Search(string widget, string text)
        {
            Click(Find(widget, "/search"));
            Input.GetTypedCharacters().Returns(text);
            Frame();
            Input.GetTypedCharacters().Returns("");
            Frame();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            panel.Dispose();
            Undo.Dispose();
            surface.Dispose();
        }
    }

    /// <summary>Mixed tag choices preserve unknown and owner-specific tags; filtered bulk edits share one undo step.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TagsPreserveIndependentMembershipsAndUndo(bool locked)
    {
        using var host = new Harness(locked);
        host.Click(host.Find("tags", "/button"));
        host.Click(host.Find("tags", "/option/0"));
        Assert.Equal(["A", "Unknown first"], host.First.Tags);
        Assert.Equal(["B", "Unknown second", "A"], host.Second.Tags);
        Assert.Single(host.Undo.History.UndoSteps);
        host.Undo.Undo();
        Assert.Equal(["B", "Unknown second"], host.Second.Tags);
        host.Frame();
        host.Search("tags", "Shared");
        host.Click(host.Find("tags", "/all"));
        Assert.Equal(["A", "Unknown first", "Shared"], host.First.Tags);
        Assert.Equal(["B", "Unknown second", "Shared"], host.Second.Tags);
        Assert.Single(host.Undo.History.UndoSteps);
        host.Click(host.Find("tags", "/clear"));
        Assert.Equal(["A", "Unknown first"], host.First.Tags);
        Assert.Equal(["B", "Unknown second"], host.Second.Tags);
        host.Undo.Undo();
        Assert.Contains("Shared", host.First.Tags);
        Assert.Contains("Shared", host.Second.Tags);
    }

    /// <summary>Each mixed mask keeps hidden bits while checkbox and filtered bulk changes reach all owners.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MasksPreserveIndependentBitsAndUndo(bool locked)
    {
        using var host = new Harness(locked);
        var first = host.First.GetComponent<LightComponent>()!;
        var second = host.Second.GetComponent<LightComponent>()!;
        var firstMask = first.CullingMask;
        var secondMask = second.CullingMask;
        host.Click(host.Find("layer-mask", "/button"));
        host.Click(host.Find("layer-mask", "/option/0"));
        Assert.Equal(firstMask, first.CullingMask);
        Assert.Equal(secondMask | LayerMask.FromLayer(0), second.CullingMask);
        Assert.Single(host.Undo.History.UndoSteps);
        host.Undo.Undo();
        Assert.Equal(secondMask, second.CullingMask);
        host.Frame();
        host.Search("layer-mask", "High");
        host.Click(host.Find("layer-mask", "/all"));
        Assert.Equal(firstMask | LayerMask.FromLayer(31), first.CullingMask);
        Assert.Equal(secondMask | LayerMask.FromLayer(31), second.CullingMask);
        host.Click(host.Find("layer-mask", "/clear"));
        Assert.Equal(firstMask, first.CullingMask);
        Assert.Equal(secondMask, second.CullingMask);
        host.Undo.Undo();
        Assert.True(first.CullingMask.Contains(31));
        Assert.True(second.CullingMask.Contains(31));
    }

    /// <summary>Readonly and unchanged memberships do not mutate or issue notifications.</summary>
    [Fact]
    public void MembershipEditsRespectReadonlyAndNoOpValues()
    {
        var node = new Node();
        var notifications = 0;
        var options = InspectorForms.Options(_ => notifications++);
        var field = FormField.ForMember(typeof(Node).GetProperty(nameof(Node.Tags))!, node, options);
        InspectorSelectionEdits.ApplyTags(field, [("Untagged", true), ("Absent", false)]);
        Assert.Equal(0, notifications);
        InspectorSelectionEdits.ApplyTags(field, [("Untagged", false), ("A", true)]);
        Assert.Equal(["A"], node.Tags);
        Assert.Equal(1, notifications);
        var readOnly = FormField.ForMember(typeof(Node).GetProperty(nameof(Node.Tags))!, node,
            InspectorForms.Options(readOnly: true));
        InspectorSelectionEdits.ApplyTags(readOnly, [("B", true)]);
        Assert.Equal(["A"], node.Tags);
        Assert.True(TagDrawer.Handles(field));
        Assert.False(TagDrawer.Handles(FormField.ForMember(typeof(Node).GetProperty(nameof(Node.Name))!, node)));
    }
}
