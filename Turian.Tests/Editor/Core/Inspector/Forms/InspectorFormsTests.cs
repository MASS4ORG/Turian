namespace Turian.Tests.Editor;

/// <summary>
/// Covers Turian's rules over the generic form builder: which members may be cleared to null, and that failing
/// getters and actions are reported instead of escaping into the inspector frame.
/// </summary>
public class InspectorFormsTests
{
    sealed class Holder
    {
        public Node? Target { get; set; }
        public string Name { get; set; } = "kept";
        public int? Optional { get; set; } = 3;
    }

    sealed class Failing
    {
        public int Broken
        {
            get => throw new InvalidOperationException("getter");
            set { }
        }

        [Button] public void Explode() => throw new InvalidOperationException("action");
    }

    /// <summary>Null clears only node, component and DataAsset references; other members keep their value.</summary>
    [Fact]
    public void NullClearsOnlyReferenceMembers()
    {
        var holder = new Holder { Target = new Node() };
        var fields = InspectorForms.Build(holder).Sections[0].Fields;

        Assert.True(fields.Single(f => f.Name == nameof(Holder.Target)).SetValue(null));
        Assert.Null(holder.Target);
        Assert.False(fields.Single(f => f.Name == nameof(Holder.Name)).SetValue(null));
        Assert.Equal("kept", holder.Name);
        Assert.False(fields.Single(f => f.Name == nameof(Holder.Optional)).SetValue(null));
        Assert.Equal(3, holder.Optional);
    }

    /// <summary>A throwing getter reads as null and a throwing action is logged, neither escapes.</summary>
    [Fact]
    public void FailuresAreReportedNotThrown()
    {
        var target = new Failing();
        var field = FormField.ForMember(typeof(Failing).GetProperty(nameof(Failing.Broken))!, target,
            InspectorForms.Options());

        Assert.Null(field.GetValue());
        var section = Assert.Single(InspectorForms.Build(target).Sections);
        Assert.Single(section.Buttons).Invoke();
    }
}
