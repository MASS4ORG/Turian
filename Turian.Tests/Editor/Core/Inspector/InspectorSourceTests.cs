namespace Turian.Tests;

/// <summary>How a source's schema becomes an Inspector form, and what survives a source that misbehaves.</summary>
public sealed class InspectorSourceTests
{
    readonly FakeInspectorSource source = new();
    readonly EditorNotifications notifications = new();
    readonly InspectorSourceCatalog catalog;

    /// <summary>Creates a catalog holding the stand-in source, as dependency injection seeds it.</summary>
    public InspectorSourceTests() => catalog = new InspectorSourceCatalog([source], notifications);

    static FormField Field(FormModel model, string label) =>
        model.Sections.SelectMany(static section => section.Fields).Single(field => field.Label == label);

    static InspectorPropertyBinding Binding(FormField field) => Assert.IsType<InspectorPropertyBinding>(field.Target);

    List<string> Reported()
    {
        var messages = new List<string>();
        while (notifications.TryTake(out var notification))
        {
            Assert.True(notification.IsError);
            messages.Add(notification.Message);
        }

        return messages;
    }

    /// <summary>A schema renders as a heading section with its actions, then one section per category.</summary>
    [Fact]
    public void ASchemaBecomesSectionsAndFields()
    {
        var subject = new FakeSubject { Visible = true };

        using var form = catalog.Build([subject])!;

        Assert.Equal(["button", "Layout"], form.Model.Sections.Select(static section => section.Title));
        Assert.Equal(["Visible", "Name"], form.Model.Sections[0].Fields.Select(static field => field.Label));
        Assert.Same(subject, form.Model.Target);
        var width = Field(form.Model, "Width");
        Assert.Equal(typeof(int), width.ValueType);
        Assert.Equal(10, width.GetValue());
        var binding = Binding(width);
        Assert.Same(subject, binding.Subject);
        Assert.Equal("length", binding.Property.Editor);
        Assert.Equal(10, binding.Property.Default);
        Assert.IsType<RangeAttribute>(Assert.Single(binding.Property.Attributes));
        var action = Assert.Single(form.Model.Sections[0].Buttons);
        Assert.True(action.IsEnabled);
        action.Invoke();
        Assert.Equal(1, source.Actions);
    }

    /// <summary>A write reaches the source only when the value differs, and a read-only property refuses it.</summary>
    [Fact]
    public void WritesGoToTheSourceWhenTheValueDiffers()
    {
        var subject = new FakeSubject();
        using var form = catalog.Build([subject])!;
        var name = Field(form.Model, "Name");

        Assert.False(name.SetValue("button"));
        Assert.True(name.SetValue("label"));
        Assert.True(name.SetValue(null));

        Assert.Equal(["name", "name"], source.Writes);
        Assert.Null(subject.Name);
        var locked = new InspectorSourceCatalog([new ReadOnlySource()]);
        using var readOnly = locked.Build([new FakeSubject()])!;
        var field = Field(readOnly.Model, "Locked");
        Assert.True(field.IsReadOnly);
        Assert.False(field.SetValue("changed"));
        Assert.Empty(Reported());
        var binding = Binding(field);
        Assert.False(binding.CanReset);
        binding.Reset();
        Assert.False(binding.IsSet);
        Assert.Empty(readOnly.Model.Sections[0].Buttons);
    }

    sealed class ReadOnlySource : IInspectorSource
    {
        public Type SelectionType => typeof(FakeSubject);

        public InspectorSchema Describe(object selection) => new("Locked",
        [
            new("locked", "Locked", typeof(string), static () => "value",
                static _ => throw new InvalidOperationException())
            {
                IsReadOnly = true,
                Reset = static () => throw new InvalidOperationException(),
            },
        ]);
    }

    sealed class RefusingSource : IInspectorSource
    {
        public Type SelectionType => typeof(FakeSubject);

        public InspectorSchema Describe(object selection)
        {
            var subject = (FakeSubject)selection;
            return new("Refusing",
            [
                new("width", "Width", typeof(int), () => subject.Width,
                    static _ => throw new NotSupportedException("refused")),
            ]);
        }
    }

    /// <summary>A setter that throws is reported, and the object and the field keep the value they had.</summary>
    [Fact]
    public void AFailingSetterLeavesTheValueAsItWas()
    {
        var subject = new FakeSubject { Width = 10 };
        using var form = new InspectorSourceCatalog([new RefusingSource()], notifications).Build([subject])!;
        var width = Field(form.Model, "Width");

        Assert.False(width.SetValue(30));

        Assert.Contains("refused", Assert.Single(Reported()));
        Assert.Equal(10, subject.Width);
        Assert.Equal(10, width.GetValue());
    }

    /// <summary>A change to a subject outside the form leaves the form current.</summary>
    [Fact]
    public void AnUnselectedSubjectChangingLeavesTheFormCurrent()
    {
        using var form = catalog.Build([new FakeSubject()])!;
        var other = new FakeSubject();

        other.Invalidate();

        Assert.False(form.IsStale);
    }

    /// <summary>Setting a value marks it as the object's own until it is reset.</summary>
    [Fact]
    public void ResetRemovesTheObjectsOwnValue()
    {
        var subject = new FakeSubject();
        using var form = catalog.Build([subject])!;
        var width = Field(form.Model, "Width");
        var binding = Binding(width);
        Assert.False(binding.IsSet);

        width.SetValue(40);

        Assert.True(binding.IsSet);
        Assert.True(binding.CanReset);
        binding.Reset();
        Assert.False(binding.IsSet);
        Assert.Equal(10, subject.Width);
    }

    /// <summary>Several subjects share the properties they all have, and an edit reaches every one.</summary>
    [Fact]
    public void SeveralSubjectsCombineTheirSharedProperties()
    {
        var first = new FakeSubject { Name = "first", Width = 1 };
        var second = new FakeSubject { Name = "second", Width = 2 };
        var narrow = new FakeSubject { Name = "narrow", HasWidth = false };

        using var form = catalog.Build([first, second], second)!;
        using var reduced = catalog.Build([first, narrow])!;

        Assert.Equal("2 Objects", form.Model.Sections[0].Title);
        Assert.Empty(form.Model.Sections[0].Buttons);
        var width = Field(form.Model, "Width");
        Assert.True(width.HasMixedValue);
        Assert.Equal(2, width.Sources.Count);
        width.SetValue(30);
        Assert.Equal((30, 30), (first.Width, second.Width));
        Assert.Equal(["width", "width"], source.Writes);
        Assert.False(width.HasMixedValue);
        Assert.Equal(["2 Objects"], reduced.Model.Sections.Select(static section => section.Title));
    }

    /// <summary>The source declaring the exact type wins over one declaring a base type.</summary>
    [Fact]
    public void TheMostExactSourceDescribesASelection()
    {
        var general = new FakeInspectorSource { Type = typeof(object) };
        var exact = new FakeInspectorSource();
        var sources = new InspectorSourceCatalog([general]);
        var subject = new FakeSubject();
        Assert.Same(general, sources.SourceFor(subject));

        var registration = sources.Add(exact);

        Assert.Same(exact, sources.SourceFor(subject));
        Assert.Same(general, sources.SourceFor("text"));
        var removed = new List<IInspectorSource>();
        sources.Removed += removed.Add;
        registration.Dispose();
        registration.Dispose();
        Assert.Equal([exact], removed);
        Assert.Same(general, sources.SourceFor(subject));
    }

    /// <summary>Subjects of different sources show nothing; a selection nothing describes has no form.</summary>
    [Fact]
    public void MixedOrUnknownSelectionsShowNothing()
    {
        catalog.Add(new FakeInspectorSource { Type = typeof(string) });

        using var mixed = catalog.Build([new FakeSubject(), "text"])!;

        Assert.Empty(mixed.Model.Sections);
        Assert.Null(catalog.Build([new object()]));
        Assert.Null(catalog.Build([]));
    }

    /// <summary>A dead subject is left out, and one whose life cannot be asked counts as dead.</summary>
    [Fact]
    public void DeadSubjectsAreIgnored()
    {
        var alive = new FakeSubject { Name = "alive" };
        var dead = new FakeSubject { IsAlive = false };
        var broken = new FakeSubject { AliveFailure = new NotSupportedException() };

        using var form = catalog.Build([alive, dead, broken], dead)!;

        Assert.Equal("alive", form.Model.Sections[0].Title);
        Assert.Equal(0, dead.Listeners);
        Assert.Null(catalog.Build([dead, broken]));
    }

    /// <summary>Scene objects and the studio's inspections are never subjects.</summary>
    [Fact]
    public void OnlyDescribedOrDeclaredObjectsAreSubjects()
    {
        catalog.Add(new FakeInspectorSource { Type = typeof(string) });

        Assert.True(catalog.IsSubject("described"));
        Assert.True(new InspectorSourceCatalog().IsSubject(new FakeSubject()));
        Assert.False(catalog.IsSubject(null));
        Assert.False(catalog.IsSubject(new Node()));
        Assert.False(catalog.IsSubject(new object()));
        Assert.True(catalog.IsAlive("described"));
    }

    /// <summary>A form goes stale when a subject reports a change, takes another key or dies.</summary>
    [Theory]
    [InlineData("invalidated")]
    [InlineData("key")]
    [InlineData("died")]
    [InlineData("sources")]
    [InlineData("unnamed")]
    public void AFormKnowsWhenToBeBuiltAgain(string change)
    {
        var subject = new FakeSubject();
        var form = catalog.Build([subject])!;
        Assert.False(form.IsStale);
        Assert.Equal(1, subject.Listeners);

        switch (change)
        {
            case "invalidated":
                subject.Invalidate();
                break;
            case "key":
                subject.Key = "another";
                break;
            case "died":
                subject.IsAlive = false;
                break;
            case "unnamed":
                subject.KeyFailure = new NotSupportedException();
                break;
            default:
                catalog.Add(new FakeInspectorSource { Type = typeof(string) });
                break;
        }

        Assert.True(form.IsStale);
        form.Dispose();
        Assert.Equal(0, subject.Listeners);
    }

    /// <summary>A source that cannot describe, or describes nothing, leaves an empty form.</summary>
    [Fact]
    public void AFailingDescriptionIsReportedAndShowsNothing()
    {
        source.DescribeFailure = new NotImplementedException("not yet");
        using var failed = catalog.Build([new FakeSubject()])!;
        Assert.Empty(failed.Model.Sections);
        Assert.Contains("not yet", Assert.Single(Reported()));

        source.DescribeFailure = null;
        source.DescribesNothing = true;
        using var nothing = catalog.Build([new FakeSubject()])!;
        Assert.Empty(nothing.Model.Sections);
        Assert.Empty(Reported());

        source.TypeFailure = new NotSupportedException();
        Assert.Null(catalog.Build(["text"]));
    }

    /// <summary>A getter that throws or lies shows the type's blank; a failing setter or action is reported.</summary>
    [Fact]
    public void FailingAccessorsStayInside()
    {
        using var form = new InspectorSourceCatalog([new BrokenSource()], notifications)
            .Build([new FakeSubject()])!;

        var number = Field(form.Model, "Number");
        Assert.Equal(0, number.GetValue());
        Assert.Equal(0, number.GetValue());
        Assert.Null(Field(form.Model, "Mistyped").GetValue());
        Assert.Null(Field(form.Model, "Optional").GetValue());
        Assert.Empty(Reported());

        Assert.False(number.SetValue(5));
        Assert.Contains("refused", Assert.Single(Reported()));
        var binding = Binding(number);
        Assert.False(binding.IsSet);
        binding.Reset();
        Assert.Contains("kept", Assert.Single(Reported()));

        var action = Assert.Single(form.Model.Sections[0].Buttons);
        Assert.False(action.IsEnabled);
        action.Invoke();
        Assert.Contains("jammed", Assert.Single(Reported()));
    }

    sealed class BrokenSource : IInspectorSource
    {
        public Type SelectionType => typeof(FakeSubject);

        public InspectorSchema Describe(object selection) => new("Broken",
        [
            new("number", "Number", typeof(int), static () => throw new NotSupportedException(),
                static _ => throw new NotSupportedException("refused"))
            {
                IsSet = static () => throw new NotSupportedException(),
                Reset = static () => throw new NotSupportedException("kept"),
            },
            new("mistyped", "Mistyped", typeof(string), static () => 4, static _ => { }),
            new("optional", "Optional", typeof(int?), static () => null, static _ => { }),
        ],
        [
            new Button("Run", static () => throw new NotSupportedException("jammed"),
                static () => throw new NotSupportedException()),
        ]);
    }
}
