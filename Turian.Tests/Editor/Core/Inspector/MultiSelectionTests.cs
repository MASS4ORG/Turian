namespace Turian.Tests;

/// <summary>Checks ordered selection, common fields, mixed values and edits preserving each object's other values.</summary>
public sealed class MultiSelectionTests
{
    sealed class Holder
    {
        public int Number { get; set; }
        [Show] public int ReadOnly => 4;
        public Vector3 Vector { get; set; }
        public Node? Reference { get; set; }
        public List<int> Entries { get; set; } = [];
    }

    /// <summary>Selection keeps identity order, an active object, and raises one notification per actual change.</summary>
    [Fact]
    public void OrderedSelectionTracksActiveAndToggles()
    {
        var selection = new SelectionService();
        var first = new object();
        var second = new object();
        var notifications = 0;
        selection.Changed += () => notifications++;
        selection.SetObjects([first, second, first], first);
        Assert.Equal([first, second], selection.Objects);
        Assert.Same(first, selection.ActiveObject);
        selection.SetObjects([first, second], first);
        Assert.Equal(1, notifications);
        selection.Toggle(first);
        Assert.Equal([second], selection.Objects);
        Assert.Same(second, selection.ActiveObject);
        selection.Toggle(first);
        Assert.Equal([second, first], selection.Objects);
        Assert.Same(first, selection.ActiveObject);
        selection.Add(second);
        Assert.Equal([second, first], selection.Objects);
        Assert.Same(second, selection.ActiveObject);
        selection.Select(null);
        Assert.Empty(selection.Objects);
        Assert.Null(selection.ActiveObject);
    }

    /// <summary>Ranges follow the supplied visible order in either direction and support additive selection.</summary>
    [Fact]
    public void RangeSelectionUsesVisibleOrder()
    {
        object[] visible = [new(), new(), new(), new()];
        var selection = new SelectionService();
        selection.SelectRange(visible, visible[2], visible[0]);
        Assert.Equal(visible.Take(3), selection.Objects);
        Assert.Same(visible[0], selection.ActiveObject);
        selection.SelectRange(visible, visible[3], visible[3], additive: true);
        Assert.Equal(visible, selection.Objects);
        selection.SelectRange(visible, new object(), visible[1]);
        Assert.Equal([visible[1]], selection.Objects);
        selection.SelectRange(visible, visible[1], new object());
        Assert.Equal([visible[1]], selection.Objects);
    }

    /// <summary>Layer locking removes affected nodes while retaining the selectable objects and active fallback.</summary>
    [Fact]
    public void ControllerRespectsLocksForMultiSelection()
    {
        var view = new SceneViewSettings();
        var inspector = new NodeInspectorController(new AssetManager(), viewSettings: view);
        var first = new Node { RenderLayer = 1 };
        var second = new Node { RenderLayer = 2 };
        inspector.SelectMany([first, second], second);
        view.SetLocked(2, true);
        Assert.Equal([first], inspector.SelectedNodes);
        Assert.Same(first, inspector.SelectedNode);
        inspector.SelectNode(second, additive: true);
        inspector.Select(second);
        Assert.Equal([first], inspector.SelectedNodes);
        inspector.SelectNode(null, additive: true);
        Assert.Single(inspector.SelectedNodes);
        inspector.SelectNode(first, toggle: true);
        Assert.Empty(inspector.SelectedNodes);
        inspector.SelectNode(first, additive: true);
        inspector.SelectNode(null);
        Assert.Empty(inspector.SelectedNodes);
    }

    /// <summary>Common component sections match type and occurrence independently of component ordering.</summary>
    [Fact]
    public void CommonComponentsMatchTypeAndOccurrence()
    {
        var first = new Node();
        var second = new Node();
        first.AddComponent(new LightComponent { Intensity = 2 });
        first.AddComponent(new ModelComponent());
        first.Components.Add(new LightComponent { Intensity = 3 });
        second.AddComponent(new LightComponent { Intensity = 4 });
        var form = InspectorForms.BuildForNodes([first, second]);
        Assert.Equal(2, form.Sections.Count);
        Assert.Equal("2 Objects", form.Sections[0].Title);
        var intensity = form.Sections[1].Fields.Single(field => field.Name == nameof(LightComponent.Intensity));
        Assert.True(intensity.HasMixedValue);
        Assert.True(intensity.SetValue(7f));
        Assert.False(intensity.HasMixedValue);
        Assert.Equal(7f, first.Components.OfType<LightComponent>().First().Intensity);
        Assert.Equal(3f, first.Components.OfType<LightComponent>().Last().Intensity);
        Assert.Equal(7f, second.GetComponent<LightComponent>()!.Intensity);
        Assert.Empty(InspectorForms.BuildForNodes([]).Sections);
        Assert.Equal(4, InspectorForms.BuildForNodes([first]).Sections.Count);
    }

    /// <summary>Editing one projected vector axis retains other axes and notifies every owner.</summary>
    [Fact]
    public void ProjectedAxisPreservesEachOwnersOtherAxes()
    {
        var first = new Holder { Vector = new Vector3(1, 2, 3) };
        var second = new Holder { Vector = new Vector3(1, 5, 6) };
        var changed = new List<object>();
        var fields = InspectorForms.BuildForObjects([first, second], changed.Add).Sections[0].Fields;
        var vector = fields.Single(field => field.Name == nameof(Holder.Vector));
        var x = vector.Project<Vector3, float>("X", value => value.X, (value, part) => value with { X = part });
        var y = vector.Project<Vector3, float>("Y", value => value.Y, (value, part) => value with { Y = part });
        Assert.False(x.HasMixedValue);
        Assert.True(y.HasMixedValue);
        Assert.True(x.SetValue(9f));
        Assert.Equal(new Vector3(9, 2, 3), first.Vector);
        Assert.Equal(new Vector3(9, 5, 6), second.Vector);
        Assert.Equal([first, second], changed);
        Assert.True(fields.Single(field => field.Name == nameof(Holder.ReadOnly)).IsReadOnly);
        Assert.Empty(InspectorForms.BuildForObjects([first, new Node()]).Sections);
        Assert.Empty(InspectorForms.BuildForObjects([]).Sections);
        Assert.Single(InspectorForms.BuildForObjects([first]).Sections);
    }

    /// <summary>Shared collection entries update all lists up to their common length without sharing the lists.</summary>
    [Fact]
    public void CollectionEditsUpdateEveryOwner()
    {
        var first = new Holder { Entries = [1, 2, 3] };
        var second = new Holder { Entries = [4, 5] };
        var field = InspectorForms.BuildForObjects([first, second]).Sections[0].Fields
            .Single(field => field.Name == nameof(Holder.Entries));
        var list = CollectionField.TryCreate(field)!;
        Assert.Equal(2, list.Count);
        Assert.True(list.Entries()[0].HasMixedValue);
        Assert.True(list.Entries()[0].SetValue(8));
        Assert.Equal(new[] { 8, 2, 3 }, first.Entries);
        Assert.Equal(new[] { 8, 5 }, second.Entries);
        Assert.True(list.Move(0, 1));
        Assert.True(list.RemoveAt(0));
        Assert.True(list.Add());
        Assert.Equal(new[] { 8, 3, 0 }, first.Entries);
        Assert.Equal(new[] { 8, 0 }, second.Entries);
        Assert.NotSame(first.Entries, second.Entries);
    }

    /// <summary>Direct references clear every owner while common headers show mixed enabled state.</summary>
    [Fact]
    public void SharedReferencesAndEnabledFieldsWriteToAll()
    {
        var first = new Holder { Reference = new Node() };
        var second = new Holder { Reference = new Node() };
        var fields = InspectorForms.BuildForObjects([first, second]).Sections[0].Fields;
        var reference = fields.Single(field => field.Name == nameof(Holder.Reference));
        Assert.True(reference.HasMixedValue);
        Assert.True(ReferenceField.TryCreate(reference)!.Clear());
        Assert.Null(first.Reference);
        Assert.Null(second.Reference);
        var nodes = new[] { new Node { IsActive = false }, new Node { IsActive = true } };
        var enabled = InspectorForms.BuildForNodes(nodes).Sections[0].EnabledField!;
        Assert.True(enabled.HasMixedValue);
        Assert.True(enabled.SetValue(true));
        Assert.All(nodes, node => Assert.True(node.IsActive));
    }

    /// <summary>Component creation and removal use matching occurrences on each node.</summary>
    [Fact]
    public void ComponentsCanBeAddedAndRemovedAcrossSelection()
    {
        var inspector = new NodeInspectorController(new AssetManager());
        var nodes = new[] { new Node(), new Node() };
        inspector.SelectMany(nodes);
        Assert.False(inspector.AddComponents([], typeof(LightComponent)));
        Assert.False(inspector.AddComponents(nodes, typeof(string)));
        Assert.True(inspector.AddComponents(nodes, typeof(LightComponent)));
        Assert.Contains(inspector.GetAvailableComponents(), type => type.ComponentType == typeof(ModelComponent));
        var representative = nodes[0].GetComponent<LightComponent>()!;
        Assert.Equal(2, NodeInspectorController.MatchingComponents(nodes, representative).Count);
        Assert.True(inspector.AddComponents(nodes, typeof(ModelComponent)));
        inspector.MoveComponents(nodes, representative, up: false);
        Assert.All(nodes, node => Assert.Same(node.GetComponent<LightComponent>(), node.Components[1]));
        inspector.MoveComponents(nodes, representative, up: false);
        inspector.MoveComponents(nodes, representative, up: true);
        Assert.All(nodes, node => Assert.Same(node.GetComponent<LightComponent>(), node.Components[0]));
        inspector.MoveComponents(nodes, representative, up: true);
        inspector.RemoveComponents(nodes, representative);
        Assert.All(nodes, node => Assert.Single(node.Components));
        Assert.Empty(NodeInspectorController.MatchingComponents(nodes, new LightComponent()));
    }
}
