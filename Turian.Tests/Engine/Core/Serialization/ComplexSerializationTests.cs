namespace Turian.Tests;

/// <summary>
/// Tests for complex polymorphic JSON serialization.
/// </summary>
public class ComplexSerializationTests
{
    /// <summary>
    /// Base class for testing polymorphic serialization.
    /// </summary>
    [TypeId("a3000003-0000-4000-8000-000000000004")]
    public class BaseClass : IdClass
    {
        /// <summary>
        /// Gets or sets the base name.
        /// </summary>
        public string BaseName { get; set; } = "Base";
    }

    /// <summary>
    /// Derived class for testing polymorphic serialization.
    /// </summary>
    [TypeId("a3000003-0000-4000-8000-000000000005")]
    public class DerivedClass : BaseClass
    {
        /// <summary>
        /// Gets or sets the derived value.
        /// </summary>
        public int DerivedValue { get; set; } = 42;
    }

    /// <summary>
    /// Container class for testing polymorphic collection serialization.
    /// </summary>
    [TypeId("a3000003-0000-4000-8000-000000000006")]
    public class ContainerClass : IdClass
    {
        /// <summary>
        /// Gets or sets the items collection.
        /// </summary>
        public Collection<BaseClass> Items { get; set; } = [];
    }

    /// <summary>
    /// Verifies that collections of polymorphic types are correctly serialized and deserialized.
    /// </summary>
    [Fact]
    public void PolymorphicListSerialization()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectJsonSerializer<ContainerClass>());
        options.Converters.Add(new ObjectJsonSerializer<BaseClass>());
        options.Converters.Add(new ObjectJsonSerializer<DerivedClass>());

        var container = new ContainerClass();
        container.Items.Add(new BaseClass { BaseName = "B1" });
        container.Items.Add(new DerivedClass { BaseName = "D1", DerivedValue = 100 });

        var json = JsonSerializer.Serialize(container, options);

        Assert.Contains("a3000003-0000-4000-8000-000000000004", json, StringComparison.Ordinal);
        Assert.Contains("a3000003-0000-4000-8000-000000000005", json, StringComparison.Ordinal);

        var deserialized = JsonSerializer.Deserialize<ContainerClass>(json, options);
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Items.Count);
        Assert.IsType<BaseClass>(deserialized.Items[0]);
        Assert.IsType<DerivedClass>(deserialized.Items[1]);
        Assert.Equal("B1", deserialized.Items[0].BaseName);
        Assert.Equal("D1", deserialized.Items[1].BaseName);
        Assert.Equal(100, ((DerivedClass)deserialized.Items[1]).DerivedValue);
    }
}
