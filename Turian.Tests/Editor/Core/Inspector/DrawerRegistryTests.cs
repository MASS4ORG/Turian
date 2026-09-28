namespace Turian.Tests.Editor;

/// <summary>Exercises the registration and composition path without a rendering backend.</summary>
public class DrawerRegistryTests
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    sealed class MarkerAttribute(int number) : Attribute
    {
        public int Number { get; } = number;
    }

    [AttributeUsage(AttributeTargets.Property)]
    sealed class OuterAttribute : Attribute;

    sealed class Target
    {
        [Marker(1), Outer, Marker(2)]
        public int Value { get; set; }
    }

    sealed class TraceDrawer(List<string> trace, string name, int order) : IAttributeDrawer
    {
        public int Order => order;

        public void Draw(Gui gui, FormField field, Attribute attribute, string id, Action next)
        {
            trace.Add(attribute is MarkerAttribute marker ? $"{name}{marker.Number}" : name);
            next();
            trace.Add($"/{name}");
        }
    }

    sealed class TestPropertyDrawer : IPropertyDrawer
    {
        public void Draw(Gui gui, FormField field, string id) { }
        public bool DrawValue(Gui gui, FormField field, string id) => true;
    }

    /// <summary>Attribute decorators nest in priority order and preserve repeated attributes.</summary>
    [Fact]
    public void AttributeDrawersComposeAroundOnePropertyDrawer()
    {
        var trace = new List<string>();
        AttributeDrawerRegistry.Register<MarkerAttribute>(new TraceDrawer(trace, "marker", 10));
        AttributeDrawerRegistry.Register<OuterAttribute>(new TraceDrawer(trace, "outer", -5));
        try
        {
            var field = FormBuilder.Build(new Target()).Sections[0].Fields.Single();

            AttributeDrawerRegistry.Draw(null!, field, "field", () => trace.Add("value"));

            Assert.Equal(["outer", "marker1", "marker2", "value",
                "/marker", "/marker", "/outer"], trace);
        }
        finally
        {
            AttributeDrawerRegistry.Unregister<MarkerAttribute>();
            AttributeDrawerRegistry.Unregister<OuterAttribute>();
        }
    }

    /// <summary>An extension overrides a built-in editor without changing the built-in mapping.</summary>
    [Fact]
    public void PropertyDrawerRegistrationOverridesAndRestoresValueEditors()
    {
        var original = PropertyDrawerRegistry.For(typeof(Vector3));
        var replacement = new TestPropertyDrawer();
        Assert.NotNull(original);

        PropertyDrawerRegistry.Register(typeof(Vector3), replacement);
        try
        {
            Assert.Same(replacement, PropertyDrawerRegistry.For(typeof(Vector3)));
        }
        finally
        {
            PropertyDrawerRegistry.Unregister(typeof(Vector3));
        }

        Assert.Same(original, PropertyDrawerRegistry.For(typeof(Vector3)));
    }
}
