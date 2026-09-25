namespace Turian.Tests.Editor;

/// <summary>
/// Covers how the inspector sees <c>AssetReference</c>, <c>NodeRef</c> and <c>ComponentRef</c> as one
/// editable id plus the type a candidate must satisfy.
/// </summary>
public class ReferenceFieldTests
{
    static readonly Guid someId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Each of the three reference generics is recognised, with its target type.</summary>
    [Theory]
    [InlineData(nameof(Holder.Texture), ReferenceKind.Asset, typeof(TextureAsset))]
    [InlineData(nameof(Holder.Target), ReferenceKind.Node, typeof(Node))]
    [InlineData(nameof(Holder.Camera), ReferenceKind.Component, typeof(CameraComponent))]
    [InlineData(nameof(Holder.Stats), ReferenceKind.Asset, typeof(DataAssetAsset))]
    [InlineData(nameof(Holder.DirectNode), ReferenceKind.Node, typeof(Node))]
    [InlineData(nameof(Holder.DirectCamera), ReferenceKind.Component, typeof(CameraComponent))]
    [InlineData(nameof(Holder.DirectData), ReferenceKind.Asset, typeof(DataAssetTest))]
    public void EachReferenceGenericIsRecognised(string member, ReferenceKind kind, Type targetType)
    {
        var reference = ReferenceFor(member);

        Assert.NotNull(reference);
        Assert.Equal(kind, reference.Kind);
        Assert.Equal(targetType, reference.TargetType);
    }

    /// <summary>A field of an ordinary type is not a reference.</summary>
    [Fact]
    public void APlainFieldIsNotAReference()
    {
        Assert.Null(ReferenceFor(nameof(Holder.Count)));
        Assert.False(ReferenceField.IsReference(FieldFor(nameof(Holder.Count))));
    }

    /// <summary>An unset reference reads as empty.</summary>
    [Fact]
    public void AnUnsetReferenceIsEmpty()
    {
        var reference = ReferenceFor(nameof(Holder.Target))!;

        Assert.True(reference.IsEmpty);
        Assert.Equal(Guid.Empty, reference.CurrentId);
    }

    /// <summary>Setting writes through to the underlying reference object.</summary>
    [Fact]
    public void SettingWritesThroughToTheTarget()
    {
        var holder = new Holder();
        var reference = ReferenceField.TryCreate(FieldFor(nameof(Holder.Target), holder))!;

        Assert.True(reference.Set(someId));

        Assert.Equal(someId, reference.CurrentId);
        Assert.Equal(someId, holder.Target.NodeId);
        Assert.False(reference.IsEmpty);
    }

    /// <summary>Clearing empties the reference rather than leaving the old id in place.</summary>
    [Fact]
    public void ClearingEmptiesTheReference()
    {
        var holder = new Holder { Texture = new AssetReference<TextureAsset>(someId) };
        var reference = ReferenceField.TryCreate(FieldFor(nameof(Holder.Texture), holder))!;
        Assert.False(reference.IsEmpty);

        Assert.True(reference.Clear());

        Assert.True(reference.IsEmpty);
        Assert.Equal(Guid.Empty, holder.Texture.AssetId);
    }

    /// <summary>A write notifies the editor, so dirty state and live views follow.</summary>
    [Fact]
    public void AWriteNotifiesTheEditor()
    {
        var holder = new Holder();
        var notified = new List<object>();
        var field = FormBuilder.Build(holder, notified.Add).Sections[0].Fields
            .First(f => f.Name == nameof(Holder.Target));

        ReferenceField.TryCreate(field)!.Set(someId);

        Assert.Equal([holder], notified);
    }

    /// <summary>A direct field holds the object itself and reports the owning node for a component.</summary>
    [Fact]
    public void ADirectFieldHoldsTheObject()
    {
        var holder = new Holder();
        var node = new Node();
        var camera = new CameraComponent();
        node.AddComponent(camera);

        var nodeField = ReferenceField.TryCreate(FieldFor(nameof(Holder.DirectNode), holder))!;
        var cameraField = ReferenceField.TryCreate(FieldFor(nameof(Holder.DirectCamera), holder))!;

        Assert.True(nodeField.IsDirect);
        Assert.True(nodeField.SetTarget(node));
        Assert.False(cameraField.SetTarget(node));
        Assert.True(cameraField.SetTarget(camera));
        Assert.Same(node, holder.DirectNode);
        Assert.Equal(node.Id, cameraField.CurrentId);
        Assert.True(nodeField.Clear());
        Assert.Null(holder.DirectNode);
    }

    static FormField FieldFor(string member, Holder? holder = null) =>
        FormBuilder.Build(holder ?? new Holder()).Sections[0].Fields.First(f => f.Name == member);

    static ReferenceField? ReferenceFor(string member) => ReferenceField.TryCreate(FieldFor(member));

    sealed class Holder
    {
        public AssetReference<TextureAsset> Texture { get; set; } = new();
        public NodeRef<Node> Target { get; set; } = new();
        public ComponentRef<CameraComponent> Camera { get; set; } = new();
        public DataAssetReference<DataAssetTest> Stats { get; set; } = new();
        public Node? DirectNode { get; set; }
        public CameraComponent? DirectCamera { get; set; }
        public DataAssetTest? DirectData { get; set; }
        public int Count { get; set; }
    }
}
