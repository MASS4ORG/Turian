namespace Turian.Tests.Editor;

/// <summary>
/// Covers the class-to-form model the inspector draws from: which members become fields, how they are
/// labeled, and that writing a field reaches the object and reports the change.
/// </summary>
public class FormBuilderTests
{
    sealed class Target
    {
        public float Speed { get; set; } = 1f;
        public Vector3 StartPosition { get; set; }
        public string Label { get; set; } = string.Empty;

        [HideInEditor] public int Hidden { get; set; }

        [ShowInEditor] bool Revealed { get; set; }
        internal const string RevealedMemberName = nameof(Revealed);

        public int ReadOnly => 42;
        internal int NotPublic { get; set; }
    }

    sealed class WithCollections
    {
        public List<string> Names { get; set; } = ["a"];
        public Dictionary<string, int> Scores { get; set; } = new() { ["one"] = 1 };
    }

    static FormField Field(FormModel model, string name) =>
        model.Sections[0].Fields.Single(field => field.Name == name);

    /// <summary>Public read-write members become fields.</summary>
    [Fact]
    public void PublicReadWriteMembersBecomeFields()
    {
        var model = FormBuilder.Build(new Target());

        Assert.Contains(model.Sections[0].Fields, field => field.Name == nameof(Target.Speed));
        Assert.Contains(model.Sections[0].Fields, field => field.Name == nameof(Target.Label));
    }

    /// <summary>Hidden, read-only and non-public members are left out.</summary>
    [Fact]
    public void HiddenReadOnlyAndNonPublicMembersAreSkipped()
    {
        var model = FormBuilder.Build(new Target());
        var names = model.Sections[0].Fields.Select(field => field.Name).ToList();

        Assert.DoesNotContain(nameof(Target.Hidden), names);
        Assert.DoesNotContain(nameof(Target.ReadOnly), names);
        Assert.DoesNotContain(nameof(Target.NotPublic), names);
    }

    /// <summary>A private member opts in with ShowInEditor.</summary>
    [Fact]
    public void ShowInEditorRevealsAPrivateMember()
    {
        var model = FormBuilder.Build(new Target());

        Assert.Contains(model.Sections[0].Fields, field => field.Name == Target.RevealedMemberName);
    }

    /// <summary>The field carries the member's type, which is what picks a drawer.</summary>
    [Fact]
    public void AFieldReportsTheTypeThatSelectsItsDrawer()
    {
        var model = FormBuilder.Build(new Target());

        Assert.Equal(typeof(float), Field(model, nameof(Target.Speed)).ValueType);
        Assert.Equal(typeof(Vector3), Field(model, nameof(Target.StartPosition)).ValueType);
    }

    /// <summary>Labels are humanized from the member name.</summary>
    [Fact]
    public void LabelsAreHumanised()
    {
        var model = FormBuilder.Build(new Target());

        Assert.Equal("Start Position", Field(model, nameof(Target.StartPosition)).Label);
        Assert.Equal("Speed", Field(model, nameof(Target.Speed)).Label);
    }

    /// <summary>Reading and writing a field goes through to the object.</summary>
    [Fact]
    public void AFieldReadsAndWritesTheTarget()
    {
        var target = new Target();
        var model = FormBuilder.Build(target);
        var speed = Field(model, nameof(Target.Speed));

        Assert.Equal(1f, speed.GetValue());
        Assert.True(speed.SetValue(4.5f));
        Assert.Equal(4.5f, target.Speed);
    }

    /// <summary>Writing notifies the editor, which is how dirty state and live views follow.</summary>
    [Fact]
    public void WritingAFieldNotifiesTheEditor()
    {
        var target = new Target();
        object? mutated = null;
        var model = FormBuilder.Build(target, changed => mutated = changed);

        Field(model, nameof(Target.Speed)).SetValue(2f);

        Assert.Same(target, mutated);
    }

    /// <summary>A node's own members come first, then one removable section per component.</summary>
    [Fact]
    public void ANodeFormPutsItsComponentsInRemovableSections()
    {
        var node = new Node { Name = "Player" };
        node.Components.Add(new CameraComponent());

        var model = FormBuilder.BuildForNode(node);

        Assert.Equal(2, model.Sections.Count);
        Assert.Equal("Player", model.Sections[0].Title);
        Assert.False(model.Sections[0].Removable);
        Assert.Equal(nameof(CameraComponent), model.Sections[1].Title);
        Assert.True(model.Sections[1].Removable);
    }

    /// <summary>The member list is cached, so a per-frame inspector does not re-reflect.</summary>
    [Fact]
    public void TheMemberListIsCachedPerType()
    {
        var first = FormBuilder.EditableMembers(typeof(Target));
        var second = FormBuilder.EditableMembers(typeof(Target));

        Assert.Same(first, second);
    }

    /// <summary>Editing a node's name through the form writes it to the node itself.</summary>
    [Fact]
    public void EditingANodeNameThroughTheFormReachesTheNode()
    {
        var node = new Node { Name = "Old" };
        var model = FormBuilder.BuildForNode(node);
        var name = model.Sections[0].Fields.Single(f => f.Name == nameof(Node.Name));

        Assert.True(name.SetValue("New"));

        Assert.Equal("New", node.Name);
    }

    /// <summary>
    /// A component's on/off switch reaches the form even though it is hidden from the body, because the
    /// section heading draws it as a checkbox beside the component's name.
    /// </summary>
    [Fact]
    public void AComponentSectionCarriesItsOwnActiveSwitch()
    {
        var component = new CameraComponent();
        var node = new Node();
        node.Components.Add(component);

        var section = FormBuilder.BuildForNode(node).Sections[1];

        Assert.NotNull(section.EnabledField);
        Assert.True(section.EnabledField!.SetValue(false));
        Assert.False(component.IsActive);
        Assert.DoesNotContain(section.BodyFields, field => field.Name == nameof(Component.IsActive));
    }

    /// <summary>A list member is editable entry by entry, and may grow and shrink.</summary>
    [Fact]
    public void AListMemberBecomesAResizableCollection()
    {
        var target = new WithCollections();
        var collection = CollectionField.TryCreate(
            Field(FormBuilder.Build(target), nameof(WithCollections.Names)));

        Assert.NotNull(collection);
        Assert.False(collection.IsDictionary);
        Assert.True(collection.Entries().Single().SetValue("b"));
        Assert.Equal("b", target.Names[0]);

        Assert.True(collection.Add());
        Assert.Equal(2, target.Names.Count);
    }

    /// <summary>A dictionary is the same thing keyed by name, with an invented key for a new entry.</summary>
    [Fact]
    public void ADictionaryMemberIsEditedThroughItsKeys()
    {
        var target = new WithCollections();
        var collection = CollectionField.TryCreate(
            Field(FormBuilder.Build(target), nameof(WithCollections.Scores)));

        Assert.NotNull(collection);
        Assert.True(collection.IsDictionary);

        var entry = collection.Entries().Single();
        Assert.Equal("one", entry.Label);
        Assert.True(entry.SetValue(7));
        Assert.Equal(7, target.Scores["one"]);

        Assert.True(collection.Add());
        Assert.Equal(2, collection.Count);
        Assert.True(collection.RemoveAt(0));
        Assert.Equal(1, collection.Count);
    }

    /// <summary>Editing a transform in place reports the change, so views bound to it refresh.</summary>
    [Fact]
    public void TouchingAFieldReportsAnInPlaceEdit()
    {
        var node = new Node();
        object? mutated = null;
        var model = FormBuilder.BuildForNode(node, changed => mutated = changed);
        var transform = model.Sections[0].Fields.Single(f => f.Name == nameof(Node.Transform));

        transform.Touch();

        Assert.Same(node, mutated);
    }
}
