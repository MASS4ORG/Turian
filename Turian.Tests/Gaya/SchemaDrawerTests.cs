namespace Turian.Tests;

/// <summary>The editors of described properties: a slider for ranges and a multi-line box for long text.</summary>
[Collection(SerialTests.Name)]
public sealed class SchemaDrawerTests : IDisposable
{
    sealed class Values : IInspectorSubject
    {
        public int Count { get; set; } = 10;

        public byte Small { get; set; } = 1;

        public float? Ratio { get; set; }

        public string Note { get; set; } = "one";

        public string Key => "values";

        public bool IsAlive => true;

        public event Action? Invalidated { add { } remove { } }
    }

    sealed class ValuesSource : IInspectorSource
    {
        public Type SelectionType => typeof(Values);

        public InspectorSchema Describe(object selection)
        {
            var values = (Values)selection;
            return new("Values",
            [
                new("count", "Count", typeof(int), () => values.Count, value => values.Count = (int)value!)
                {
                    Attributes = [new RangeAttribute(0, 100)],
                },
                new("small", "Small", typeof(byte), () => values.Small, value => values.Small = (byte)value!)
                {
                    Attributes = [new RangeAttribute(0, 1000)],
                },
                new("ratio", "Ratio", typeof(float?), () => values.Ratio, value => values.Ratio = (float?)value)
                {
                    Attributes = [new RangeAttribute(0.5f, 1.5f)],
                },
                new("note", "Note", typeof(string), () => values.Note, value => values.Note = (string)value!)
                {
                    Attributes = [new TextAreaAttribute(2, 4)],
                },
            ]);
        }
    }

    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(640, 600));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);
    readonly IInputHandler input = Substitute.For<IInputHandler>();
    readonly Gui gui;
    readonly InspectorSourceCatalog catalog = new([new ValuesSource()]);
    readonly FormRenderContext context = new() { Drawers = new FormDrawers(TurianForms.Drawers) };

    /// <summary>Gives the headless GUI a pointer the tests move.</summary>
    public SchemaDrawerTests()
    {
        input.MousePosition.Returns(new Vector2(-1));
        input.GetTypedCharacters().Returns("");
        gui = new Gui { Input = input };
    }

    /// <inheritdoc />
    public void Dispose() => surface.Dispose();

    void Frame(InspectorForm form) => InspectorFormsRenderingTests.Frame(gui, surface, font, drawn =>
    {
        using (drawn.Node(600, -1, "form").Direction(Axis.Vertical).Enter())
        {
            for (var i = 0; i < form.Model.Sections[0].Fields.Count; i++)
            {
                drawn.FormField(form.Model.Sections[0].Fields[i], $"f{i}", context);
            }
        }
    });

    IEnumerable<LayoutNode> Nodes(LayoutNode node) => new[] { node }.Concat(node.Children.SelectMany(Nodes));

    Rect Rect(string id) => Nodes(gui.RootNode!).First(node => node.Id.EndsWith(id, StringComparison.Ordinal)).Rect;

    void Press(InspectorForm form, string id, float fraction)
    {
        var rect = Rect(id);
        input.MousePosition.Returns(new Vector2(rect.X + rect.W * fraction, rect.Y + rect.H / 2));
        Frame(form);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        Frame(form);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
        Frame(form);
    }

    /// <summary>Pressing a slider writes the value at that point, rounded for a whole number.</summary>
    [Fact]
    public void ARangeIsEditedWithASlider()
    {
        var values = new Values();
        using var form = catalog.Build([values])!;
        Frame(form);
        Assert.Equal(10, values.Count);

        Press(form, "f0/slider", 0.4f);

        Assert.InRange(values.Count, 20, 45);
        Press(form, "f2/slider", 0.99f);
        Assert.InRange(values.Ratio!.Value, 1f, 1.5f);
    }

    /// <summary>A slider position the property's type cannot hold writes nothing.</summary>
    [Fact]
    public void AValueBeyondTheTypeIsNotWritten()
    {
        var values = new Values();
        using var form = catalog.Build([values])!;
        Frame(form);

        Press(form, "f1/slider", 0.8f);

        Assert.Equal(1, values.Small);
    }

    /// <summary>Pressing a slider over several objects with different values writes every one.</summary>
    [Fact]
    public void ASliderEditsEverySelectedObject()
    {
        var first = new Values { Count = 1 };
        var second = new Values { Count = 90 };
        using var form = catalog.Build([first, second])!;
        Frame(form);

        Press(form, "f0/slider", 0.5f);

        Assert.Equal(first.Count, second.Count);
        Assert.InRange(first.Count, 30, 70);
    }

    /// <summary>Long text has a box as tall as its lines within bounds, and typing in it writes the property.</summary>
    [Fact]
    public void LongTextIsEditedInATextArea()
    {
        var values = new Values();
        using var form = catalog.Build([values])!;
        Frame(form);
        var low = Rect("f3/text").H;
        values.Note = "1\n2\n3\n4\n5\n6\n7";
        Frame(form);
        var high = Rect("f3/text").H;
        Assert.True(high > low);

        values.Note = "a";
        Press(form, "f3/text", 0.9f);
        input.GetTypedCharacters().Returns("b");
        Frame(form);
        input.GetTypedCharacters().Returns("");
        Frame(form);

        Assert.Equal(2, values.Note.Length);
        Assert.Contains("b", values.Note, StringComparison.Ordinal);
    }

    /// <summary>Only writable described properties carrying the hint get these editors.</summary>
    [Fact]
    public void OtherFieldsKeepTheirOwnEditors()
    {
        var subject = new FakeSubject();
        using var form = new InspectorSourceCatalog([new FakeInspectorSource()]).Build([subject])!;
        var fields = form.Model.Sections.SelectMany(static section => section.Fields).ToList();
        var name = fields.Single(static field => field.Label == "Name");
        var width = fields.Single(static field => field.Label == "Width");

        Assert.True(SchemaRangeDrawer.Takes(width));
        Assert.False(SchemaRangeDrawer.Takes(name));
        Assert.False(SchemaTextAreaDrawer.Takes(name));
        Assert.False(SchemaTextAreaDrawer.Takes(width));
    }
}
