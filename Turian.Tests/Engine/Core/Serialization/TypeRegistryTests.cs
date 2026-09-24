namespace Turian.Tests;

/// <summary>
/// Tests the <see cref="TypeRegistry"/> Guid &lt;-&gt; Type mapping that backs polymorphic JSON
/// serialization.
/// </summary>
public class TypeRegistryTests
{
    [TypeId("b0000000-0000-4000-8000-000000000001")]
    sealed class TaggedSampleA : IdClass;

    [TypeId("b0000000-0000-4000-8000-000000000002")]
    sealed class TaggedSampleB : IdClass;

    sealed class UntaggedSample : IdClass;

    // Used only by RegisterOverwritesPreviousMapping so the assertion does not pollute
    // mappings other tests rely on.
    sealed class OverwriteTargetX : IdClass;
    sealed class OverwriteTargetY : IdClass;

    /// <summary>Annotated types are discoverable by their stable id.</summary>
    [Fact]
    public void AnnotatedTypeResolvesById()
    {
        var resolved = TypeRegistry.GetTypeOrThrow(Guid.Parse("b0000000-0000-4000-8000-000000000001"));
        Assert.Equal(typeof(TaggedSampleA), resolved);
    }

    /// <summary>Annotated types report their declared id back through the registry.</summary>
    [Fact]
    public void AnnotatedTypeReportsItsId()
    {
        var id = TypeRegistry.GetIdOrThrow(typeof(TaggedSampleB));
        Assert.Equal(Guid.Parse("b0000000-0000-4000-8000-000000000002"), id);
    }

    /// <summary>Looking up an unknown id returns false.</summary>
    [Fact]
    public void TryGetTypeReturnsFalseForUnknownId()
    {
        var found = TypeRegistry.TryGetType(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"), out var type);
        Assert.False(found);
        Assert.Null(type);
    }

    /// <summary>Asking for the id of an untagged type throws a clear, actionable error.</summary>
    [Fact]
    public void UnannotatedTypeThrowsOnGetId()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TypeRegistry.GetIdOrThrow(typeof(UntaggedSample)));
        Assert.Contains("[TypeId", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Empty Guids cannot be registered (would defeat polymorphism).</summary>
    [Fact]
    public void RegisterEmptyGuidThrows()
    {
        Assert.Throws<ArgumentException>(() => TypeRegistry.Register(Guid.Empty, typeof(TaggedSampleA)));
    }

    /// <summary>Re-registering the same id with a new type wins (hot-reload semantics).</summary>
    [Fact]
    public void RegisterOverwritesPreviousMapping()
    {
        var id = Guid.Parse("b0000000-0000-4000-8000-00000000aaaa");
        TypeRegistry.Register(id, typeof(OverwriteTargetX));
        TypeRegistry.Register(id, typeof(OverwriteTargetY));
        Assert.Equal(typeof(OverwriteTargetY), TypeRegistry.GetTypeOrThrow(id));
    }
}
