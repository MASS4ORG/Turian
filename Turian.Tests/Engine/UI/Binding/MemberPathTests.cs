namespace Turian.Tests;

/// <summary>Covers missing member paths and reflection assignments used by UI data bindings.</summary>
public sealed class MemberPathTests
{
    sealed class Model
    {
        /// <summary>The nested binding target.</summary>
        public Model? Child { get; set; }

        /// <summary>A writable numeric field.</summary>
        public int Count;

        /// <summary>A nullable binding value.</summary>
        public int? Optional { get; set; }

        /// <summary>An enum binding value.</summary>
        public BindingMode Mode { get; set; }

        /// <summary>A read-only property cannot be assigned.</summary>
        public int ReadOnly => 7;
    }

    /// <summary>Paths read properties, fields and both dictionary forms, with null for missing segments.</summary>
    [Fact]
    public void Read_MemberKinds()
    {
        var model = new Model { Count = 3, Child = new Model { Count = 4 } };
        Assert.Equal(3, MemberPath.Read(model, "Count"));
        Assert.Equal(4, MemberPath.Read(model, "Child.Count"));
        Assert.Same(model, MemberPath.Read(model, ""));
        Assert.Null(MemberPath.Read(null, "Child.Count"));
        Assert.Null(MemberPath.Read(model, "Missing"));
        Assert.Null(MemberPath.Read(model, "Missing.Count"));
        var dictionary = new Dictionary<string, object?> { ["model"] = model };
        Assert.Equal(3, MemberPath.Read(dictionary, "model.Count"));
        Assert.Null(MemberPath.Read(dictionary, "missing"));
        var plain = new System.Collections.Hashtable { ["count"] = 5 };
        Assert.Equal(5, MemberPath.Read(plain, "count"));
        Assert.Null(MemberPath.Read(plain, "missing"));
    }

    /// <summary>Assignments convert fields, nullable properties and enums and can add dictionary keys.</summary>
    [Fact]
    public void Write_MemberKinds()
    {
        var model = new Model { Child = new Model() };
        Assert.True(MemberPath.TryWrite(model, "Child.Count", "12"));
        Assert.Equal(12, model.Child.Count);
        Assert.True(MemberPath.TryWrite(model, "Optional", "5"));
        Assert.Equal(5, model.Optional);
        Assert.True(MemberPath.TryWrite(model, "Optional", null));
        Assert.Null(model.Optional);
        Assert.True(MemberPath.TryWrite(model, "Mode", "twoway"));
        Assert.Equal(BindingMode.TwoWay, model.Mode);
        Assert.True(MemberPath.TryWrite(model, "Count", 9));
        Assert.Equal(9, model.Count);
        var dictionary = new Dictionary<string, object?>();
        Assert.True(MemberPath.TryWrite(dictionary, "new", model));
        Assert.Same(model, dictionary["new"]);
    }

    /// <summary>Missing roots, paths, intermediate targets and writable members reject assignment.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Missing")]
    [InlineData("Child.Count")]
    [InlineData("ReadOnly")]
    public void Write_RejectsMissingTargets(string path)
    {
        Assert.False(MemberPath.TryWrite(new Model(), path, 1));
        Assert.False(MemberPath.TryWrite(null, path, 1));
    }
}
