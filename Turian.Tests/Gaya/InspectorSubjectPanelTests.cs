using DocumentInfo = Gaya.Sdk.DocumentInfo;

namespace Turian.Tests;

/// <summary>Draws the Inspector over selections an inspector source describes, and follows their documents.</summary>
[Collection(SerialTests.Name)]
public sealed class InspectorSubjectPanelTests
{
    sealed class Harness : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(640, 900));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly IInputHandler input = Substitute.For<IInputHandler>();
        readonly Gui gui;
        readonly DocumentSelectionMemory memory;

        internal readonly FakeInspectorSource Source = new();
        internal readonly InspectorSourceCatalog Sources = new();
        internal readonly DocumentService Documents = new();
        internal readonly NodeInspectorController Selection;
        internal readonly UndoService Undo;
        internal readonly InspectorPanel Panel;
        internal readonly IDisposable Registration;

        internal Harness()
        {
            var assets = new AssetManager();
            var loader = Substitute.For<IAssetLoader>();
            var database = new AssetDatabase();
            var tree = new SceneTreeController(assets, new SettingsService(), null!, loader,
                Substitute.For<ISceneManager>(), database);
            Selection = new NodeInspectorController(assets);
            Undo = new UndoService(tree, Selection, assets, loader);
            Registration = Sources.Add(Source);
            memory = new DocumentSelectionMemory(Documents, Selection, tree, Sources);
            Panel = new InspectorPanel(Selection, assets, new ReferencePicker(null!, null!, null!), null!, null!,
                new InspectorSettings { AutoExpandComponents = false }, null!, null!, Undo, null!, null!, null!,
                database, null, Sources, Documents);
            input.MousePosition.Returns(new Vector2(-1));
            input.GetTypedCharacters().Returns("");
            gui = new Gui { Input = input };
            Documents.Open(new DocumentInfo("menu", "test.fake", "Menu"));
        }

        internal FakeSubject Select(string name = "button")
        {
            var subject = new FakeSubject { Name = name };
            Selection.Select(subject);
            Frame();
            return subject;
        }

        internal void Frame()
        {
            InspectorFormsRenderingTests.Frame(gui, surface, font, Panel.Render);
            Undo.Flush();
        }

        internal bool Shows(string id) => Nodes(gui.RootNode!).Any(node => node.Id == id);

        internal LayoutNode Find(string id) => Nodes(gui.RootNode!).Single(node => node.Id == id);

        internal void Click(Vector2 position, GMouseButton button = GMouseButton.Left)
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

        internal FormModel Model => (FormModel)typeof(InspectorPanel)
            .GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Panel)!;

        static IEnumerable<LayoutNode> Nodes(LayoutNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Nodes));

        public void Dispose()
        {
            Panel.Dispose();
            memory.Dispose();
            Undo.Dispose();
            surface.Dispose();
        }
    }

    /// <summary>The panel draws the schema's sections; a click writes through the source, not the undo service.</summary>
    [Fact]
    public void ASubjectIsEditedThroughItsSourceWithoutUndoSteps()
    {
        using var host = new Harness();
        var subject = host.Select();

        Assert.True(host.Shows("inspector/section0/field1/prefab"));
        Assert.True(host.Shows("inspector/section1/field0/prefab"));
        var editor = host.Find("inspector/section0/field0/editor").Rect;
        host.Click(new Vector2(editor.X + 6, editor.Y + editor.H / 2));

        Assert.True(subject.Visible);
        Assert.Equal(["visible"], host.Source.Writes);
        Assert.Empty(host.Undo.History.UndoSteps);
        Assert.Equal(1, host.Source.Descriptions);
    }

    /// <summary>Hovering a described property shows its tooltip, and its menu resets the object's own value.</summary>
    [Fact]
    public void APropertyWithItsOwnValueCanBeReset()
    {
        using var host = new Harness();
        var subject = host.Select();
        host.Model.Sections[1].Fields[0].SetValue(40);
        host.Frame();
        var row = host.Find("inspector/section1/field0/prefab").Rect;

        var position = new Vector2(row.X + 20, row.Y + row.H / 2);

        host.Click(position, GMouseButton.Right);
        host.Click(position + new Vector2(14, 12));

        Assert.Equal(10, subject.Width);
        Assert.False(subject.WidthSet);
        Assert.Equal(["width", "reset"], host.Source.Writes);
        Assert.Empty(host.Undo.History.UndoSteps);
    }

    /// <summary>Components start folded when the setting says so, while a subject's categories stay open.</summary>
    [Fact]
    public void ComponentsStartFoldedButSubjectCategoriesDoNot()
    {
        using var host = new Harness();
        host.Select();
        Assert.True(host.Shows("inspector/section1/field0/prefab"));

        var node = new Node();
        node.AddComponent(new LightComponent());
        host.Selection.Select(node);
        host.Frame();

        Assert.True(host.Shows("inspector/section1"));
        Assert.False(host.Shows("inspector/section1/field0/prefab"));
    }

    /// <summary>A subject that reports a change is described again and the panel shows the new properties.</summary>
    [Fact]
    public void AnInvalidatedSubjectIsRebuilt()
    {
        using var host = new Harness();
        var subject = host.Select();

        subject.HasWidth = false;
        host.Frame();
        Assert.True(host.Shows("inspector/section1/field0/prefab"));
        subject.Invalidate();
        host.Frame();

        Assert.Equal(2, host.Source.Descriptions);
        Assert.False(host.Shows("inspector/section1/field0/prefab"));
    }

    /// <summary>Several subjects are shown together, and replacing one rebuilds the form.</summary>
    [Fact]
    public void SeveralSubjectsAreShownTogether()
    {
        using var host = new Harness();
        var first = new FakeSubject { Name = "first" };
        var second = new FakeSubject { Name = "second" };
        host.Selection.SelectMany([first, second], second);
        host.Frame();
        Assert.Equal("2 Objects", host.Model.Sections[0].Title);

        host.Selection.SelectMany([new FakeSubject(), second], second);
        host.Frame();

        Assert.Equal(0, first.Listeners);
        Assert.Equal(1, second.Listeners);
    }

    /// <summary>One click that edits several subjects is one step in their document's history.</summary>
    [Fact]
    public void AMultiSelectionEditIsOneStep()
    {
        using var host = new Harness();
        var history = new DocumentHistory();
        var first = new FakeSubject { Name = "first", Log = history };
        var second = new FakeSubject { Name = "second", Log = history };
        host.Selection.SelectMany([first, second], second);
        host.Frame();
        var editor = host.Find("inspector/section0/field0/editor").Rect;

        host.Click(new Vector2(editor.X + 6, editor.Y + editor.H / 2));

        Assert.True(first.Visible);
        Assert.True(second.Visible);
        Assert.Equal("Edit 2 Objects", history.UndoLabel);
        history.Undo();
        Assert.False(first.Visible);
        Assert.False(second.Visible);
        Assert.False(history.CanUndo);
    }

    /// <summary>A change to an object outside the selection does not draw the form again.</summary>
    [Fact]
    public void AnUnselectedSubjectChangingDoesNotRebuildTheForm()
    {
        using var host = new Harness();
        host.Select();

        new FakeSubject { Name = "other" }.Invalidate();
        host.Frame();

        Assert.Equal(1, host.Source.Descriptions);
    }

    /// <summary>Several objects whose shared property differs show that property as mixed in the panel.</summary>
    [Fact]
    public void DifferingValuesShowAsMixedInThePanel()
    {
        using var host = new Harness();
        var first = new FakeSubject { Width = 1 };
        var second = new FakeSubject { Width = 2 };
        host.Selection.SelectMany([first, second], second);
        host.Frame();

        var width = host.Model.Sections.SelectMany(static section => section.Fields)
            .Single(static field => field.Label == "Width");
        Assert.True(width.HasMixedValue);
    }

    /// <summary>A dead subject is not shown, and nothing keeps listening to it.</summary>
    [Fact]
    public void ADeadSubjectIsIgnored()
    {
        using var host = new Harness();
        var subject = host.Select();

        subject.IsAlive = false;
        host.Frame();

        Assert.False(host.Shows("inspector/section0/field0/prefab"));
        Assert.Equal(0, subject.Listeners);

        // Locking refuses a dead subject instead of pinning it.
        host.Panel.Locked = true;
        Assert.False(host.Panel.Locked);
    }

    /// <summary>Leaving or closing a document deselects its subjects but leaves other selections alone.</summary>
    [Theory]
    [InlineData("switch")]
    [InlineData("close")]
    [InlineData("deactivate")]
    public void LeavingADocumentDeselectsItsSubjects(string how)
    {
        using var host = new Harness();
        var subject = host.Select();

        switch (how)
        {
            case "switch":
                host.Documents.Open(new DocumentInfo("other", "test.fake", "Other"));
                break;
            case "close":
                host.Documents.Close("menu");
                break;
            default:
                host.Documents.Deactivate();
                break;
        }

        host.Frame();
        Assert.Empty(host.Selection.Selection.Objects);
        Assert.Equal(0, subject.Listeners);

        var asset = new object();
        host.Selection.Select(asset);
        host.Documents.Open(new DocumentInfo("third", "test.fake", "Third"));
        host.Documents.Deactivate();
        Assert.Same(asset, host.Selection.SelectedObject);
    }

    /// <summary>A locked panel keeps its subject across documents and lets go when that document closes.</summary>
    [Fact]
    public void ALockEndsWhenTheSubjectsDocumentCloses()
    {
        using var host = new Harness();
        var subject = host.Select();
        host.Panel.Locked = true;

        host.Documents.Open(new DocumentInfo("other", "test.fake", "Other"));
        host.Frame();
        Assert.True(host.Panel.Locked);
        Assert.True(host.Shows("inspector/section0/field0/prefab"));

        host.Documents.Close("menu");
        host.Frame();

        Assert.False(host.Panel.Locked);
        Assert.False(host.Shows("inspector/section0/field0/prefab"));
        Assert.Equal(0, subject.Listeners);
    }

    /// <summary>Removing a source deselects what it described and releases a lock on it.</summary>
    [Fact]
    public void RemovingASourceLetsItsSelectionsGo()
    {
        using var host = new Harness();
        var subject = host.Select();
        host.Panel.Locked = true;

        host.Registration.Dispose();
        host.Frame();

        Assert.Empty(host.Selection.Selection.Objects);
        Assert.False(host.Panel.Locked);
        Assert.Equal(0, subject.Listeners);
    }
}
