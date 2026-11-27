namespace Turian.Tests;

/// <summary>A selected element of a stand-in document.</summary>
class FakeSubject : IInspectorSubject
{
    public string Name { get; set; } = "button";

    public int Width { get; set; } = 10;

    public bool Visible { get; set; }

    /// <summary>Whether the schema also lists a property only some elements have.</summary>
    public bool HasWidth { get; set; } = true;

    public bool WidthSet { get; set; }

    public Exception? AliveFailure { get; set; }

    public Exception? KeyFailure { get; set; }

    bool alive = true;
    string key = "element";

    /// <summary>The followers of <see cref="Invalidated"/>.</summary>
    public int Listeners => Invalidated?.GetInvocationList().Length ?? 0;

    /// <inheritdoc />
    public string Key
    {
        get => KeyFailure is { } failure ? throw failure : key;
        set => key = value;
    }

    /// <inheritdoc />
    public bool IsAlive
    {
        get => AliveFailure is { } failure ? throw failure : alive;
        set => alive = value;
    }

    /// <inheritdoc />
    public event Action? Invalidated;

    public void Invalidate() => Invalidated?.Invoke();
}

/// <summary>Describes <see cref="FakeSubject"/>s, counting the changes its setters were asked to make.</summary>
sealed class FakeInspectorSource : IInspectorSource
{
    /// <summary>The ids of the properties written, in order.</summary>
    public List<string> Writes { get; } = [];

    public int Descriptions { get; private set; }

    public int Actions { get; private set; }

    public Exception? DescribeFailure { get; set; }

    public bool DescribesNothing { get; set; }

    public Exception? TypeFailure { get; set; }

    public Type Type { get; init; } = typeof(FakeSubject);

    /// <inheritdoc />
    public Type SelectionType => TypeFailure is { } failure ? throw failure : Type;

    /// <inheritdoc />
    public InspectorSchema? Describe(object selection)
    {
        Descriptions++;
        if (DescribeFailure is { } failure)
        {
            throw failure;
        }

        if (DescribesNothing)
        {
            return null;
        }

        var subject = (FakeSubject)selection;
        var properties = new List<InspectorProperty>
        {
            new("visible", "Visible", typeof(bool), () => subject.Visible, value =>
            {
                Writes.Add("visible");
                subject.Visible = (bool)value!;
            }),
            new("name", "Name", typeof(string), () => subject.Name, value =>
            {
                Writes.Add("name");
                subject.Name = (string)value!;
            }),
        };
        if (subject.HasWidth)
        {
            properties.Add(new InspectorProperty("width", "Width", typeof(int), () => subject.Width, value =>
            {
                Writes.Add("width");
                subject.Width = (int)value!;
                subject.WidthSet = true;
            })
            {
                Category = "Layout",
                Tooltip = "How wide the element is.",
                Editor = "length",
                Default = 10,
                IsSet = () => subject.WidthSet,
                Reset = () =>
                {
                    Writes.Add("reset");
                    subject.Width = 10;
                    subject.WidthSet = false;
                },
                Attributes = [new RangeAttribute(0, 100)],
            });
        }

        return new InspectorSchema(subject.Name, properties,
            [new Button("Duplicate", () => Actions++, () => subject.Visible)]);
    }
}
