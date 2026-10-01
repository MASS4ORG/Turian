// The fixtures' setters deliberately discard their values.
// ReSharper disable ValueParameterNotUsed

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

        [InjectService, JsonIgnore]
        public IInputSource? Input { get; private set; }

        [ShowInEditor] bool Revealed { get; set; }
        internal const string RevealedMemberName = nameof(Revealed);

        public int ReadOnly => 42;
        internal int NotPublic { get; set; }
    }

    sealed class WithAction
    {
        public int Count { get; set; }
        [Button] public void Increment() => Count++;
    }

    /// <summary>Read-only forms reject writes and suppress annotated actions and collection mutations.</summary>
    [Fact]
    public void ReadOnlyFormsProtectFieldsCollectionsAndActions()
    {
        var target = new WithAction();
        var editable = Assert.Single(FormBuilder.Build(target).Sections);
        Assert.Single(editable.Buttons).Invoke();
        Assert.Equal(1, target.Count);
        var frozen = Assert.Single(FormBuilder.Build(target, readOnly: true).Sections);
        Assert.Empty(frozen.Buttons);
        Assert.False(Assert.Single(frozen.Fields).SetValue(2));
        Assert.Equal(1, target.Count);

        var collections = new WithCollections();
        var form = FormBuilder.Build(collections, readOnly: true);
        foreach (var field in form.Sections[0].Fields)
        {
            var collection = CollectionField.TryCreate(field)!;
            Assert.True(collection.IsReadOnly);
            Assert.False(collection.CanResize);
            Assert.False(collection.Entries()[0].SetValue("changed"));
        }
        Assert.Equal("a", collections.Names[0]);
        Assert.Equal(1, collections.Scores["one"]);
    }

    /// <summary>A calculated field reads current context and rejects writes without mutating its target.</summary>
    [Fact]
    public void CalculatedFieldsRemainLiveAndReadOnly()
    {
        var value = 2;
        var field = FormField.Display("Latest", new object(), () => value);
        Assert.Equal(2, field.GetValue());
        value = 3;
        Assert.Equal(3, field.GetValue());
        Assert.False(field.SetValue(4));
        Assert.Equal(3, value);
    }

    sealed class WithCollections
    {
        public List<string> Names { get; set; } = ["a"];
        public Dictionary<string, int> Scores { get; set; } = new() { ["one"] = 1 };
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    sealed class CustomHintAttribute(int number) : Attribute
    {
        public int Number { get; } = number;
    }

    sealed class OrderedTarget
    {
        public int DefaultFirst { get; set; }
        [InspectorOrder(10)] public int Last { get; set; }
        [InspectorOrder(-10), Range(1, 5), ReadOnly, NumericUpDown]
        [CustomHint(1), CustomHint(2)]
        public int First { get; set; }
        public int DefaultSecond { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class UnsafeTarget
    {
        public int Valid { get; set; }
        public int Throws { get => throw new ApplicationException("Not ready"); set { } }
        [ShowInEditor] public int WriteOnly { set { } }
        public Span<int> Unsupported { get => []; set { } }
        public int this[int index] { get => index; set { } }
    }

    class InheritedTarget
    {
        [Range(2, 3), InspectorOrder(-4)]
        public virtual int Ordered { get; set; }

        [HideInEditor]
        public virtual int Hidden { get; set; }
    }

    sealed class OverriddenTarget : InheritedTarget
    {
        public override int Ordered { get; set; }
        public override int Hidden { get; set; }
    }

    sealed class ChangingTarget
    {
        public bool Ready { get; set; } = true;
        public int Value { get => Ready ? 42 : throw new ApplicationException("Not ready"); set { } }
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
        Assert.DoesNotContain(nameof(Target.Input), names);
        Assert.DoesNotContain(nameof(Target.ReadOnly), names);
        Assert.DoesNotContain(nameof(Target.NotPublic), names);
    }

    /// <summary>A private member opts in with ShowInEditor.</summary>
    [Fact]
    public void ShowInEditorRevealsAPrivateMember()
    {
        var model = FormBuilder.Build(new Target());

        Assert.Contains(model.Sections[0].Fields, field => field.Name == Target.RevealedMemberName);
        Assert.Equal(false, Field(model, Target.RevealedMemberName).GetValue());
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

    /// <summary>Forms for different objects reuse the same member metadata and attributes.</summary>
    [Fact]
    public void MetadataIsCachedAndSharedBetweenForms()
    {
        var first = Field(FormBuilder.Build(new OrderedTarget()), nameof(OrderedTarget.First));
        var second = Field(FormBuilder.Build(new OrderedTarget()), nameof(OrderedTarget.First));

        Assert.Same(first.Metadata, second.Metadata);
        Assert.Same(FormBuilder.EditableMetadata(typeof(OrderedTarget)),
            FormBuilder.EditableMetadata(typeof(OrderedTarget)));
        Assert.Same(first.Attribute<RangeAttribute>(), second.Attribute<RangeAttribute>());
        Assert.Equal((1f, 5f), first.Range);
        Assert.True(first.IsReadOnly);
    }

    /// <summary>Known concerns are queryable without losing user-defined repeatable attributes.</summary>
    [Fact]
    public void MetadataGroupsAttributesWithoutDiscardingCustomOrRepeatedHints()
    {
        var metadata = Field(FormBuilder.Build(new OrderedTarget()),
            nameof(OrderedTarget.First)).Metadata!;

        Assert.Contains(metadata.Layout, attribute => attribute is InspectorOrderAttribute);
        Assert.Contains(metadata.Validation, attribute => attribute is RangeAttribute);
        Assert.Contains(metadata.Validation, attribute => attribute is ReadOnlyAttribute);
        Assert.Contains(metadata.RenderingHints, attribute => attribute is NumericUpDownAttribute);
        Assert.Equal([1, 2], metadata.GetAttributes<CustomHintAttribute>()
            .Select(attribute => attribute.Number));
        Assert.Equal(2, metadata.Attributes.Count(attribute => attribute is CustomHintAttribute));
    }

    /// <summary>Explicit priority sorts members while preserving reflection order for ties.</summary>
    [Fact]
    public void MemberPrioritySortsStablyWithoutReorderingTies()
    {
        var names = FormBuilder.Build(new OrderedTarget()).Sections[0].Fields
            .Select(field => field.Name);

        Assert.Equal([nameof(OrderedTarget.First), nameof(OrderedTarget.DefaultFirst),
            nameof(OrderedTarget.DefaultSecond), nameof(OrderedTarget.Last)], names);
    }

    /// <summary>Instance-dependent failing getters and indexers cannot break an inspector redraw.</summary>
    [Fact]
    public void UnsafePropertiesAreSkippedAfterMetadataHasBeenBuilt()
    {
        var target = new UnsafeTarget();
        var fields = FormBuilder.Build(target).Sections[0].Fields;

        Assert.Single(fields);
        Assert.Equal(nameof(UnsafeTarget.Valid), fields[0].Name);
        Assert.Equal(0, fields[0].GetValue());
        Assert.Equal([nameof(UnsafeTarget.Valid)],
            InspectorReflector.GetDisplayableMembers(target).Select(member => member.Name));
    }

    /// <summary>Overridden members retain inheritable attributes and shared metadata cannot be reordered.</summary>
    [Fact]
    public void OverriddenMemberMetadataRetainsBaseAttributes()
    {
        var metadata = FormBuilder.EditableMetadata(typeof(OverriddenTarget));

        Assert.DoesNotContain(metadata, member => member.Member.Name == nameof(OverriddenTarget.Hidden));
        var ordered = Assert.Single(metadata);
        Assert.Equal(-4, ordered.Priority);
        Assert.NotNull(ordered.GetAttribute<RangeAttribute>());
        Assert.False(metadata is InspectorMemberMetadata[]);
    }

    /// <summary>A previously safe getter can fail later without breaking the existing form.</summary>
    [Fact]
    public void AGetterThatBecomesUnsafeReturnsNoValue()
    {
        var target = new ChangingTarget();
        var field = Field(FormBuilder.Build(target), nameof(ChangingTarget.Value));
        Assert.Equal(42, field.GetValue());

        target.Ready = false;

        Assert.Null(field.GetValue());
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
