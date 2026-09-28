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

    sealed class TooltippedTarget
    {
        [Tooltip("Explains the value")]
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

    /// <summary>The built-in tooltip participates as rendering metadata and an attribute decorator.</summary>
    [Fact]
    public void TooltipIsAvailableToTheDrawerPipeline()
    {
        var field = FormBuilder.Build(new TooltippedTarget()).Sections[0].Fields.Single();

        Assert.Contains(field.Metadata!.RenderingHints, hint => hint is TooltipAttribute);
        Assert.Equal("Explains the value", field.Attribute<TooltipAttribute>()?.Text);
    }

    /// <summary>Both GUI passes can compose a tooltip around the numeric drawer.</summary>
    [Fact]
    public void TooltippedFieldRendersInBothPasses()
    {
        var field = FormBuilder.Build(new TooltippedTarget()).Sections[0].Fields.Single();
        using var surface = SKSurface.Create(new SKImageInfo(320, 120));
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        var font = Font.FromFamilyName("sans-serif", 14);
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        AttributeDrawerRegistry.Draw(gui, field, "tooltip-test",
            () => PropertyDrawerRegistry.For(field.ValueType).Draw(gui, field, "tooltip-test"));
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        AttributeDrawerRegistry.Draw(gui, field, "tooltip-test",
            () => PropertyDrawerRegistry.For(field.ValueType).Draw(gui, field, "tooltip-test"));
        gui.Render();
        gui.EndFrame();
    }

    /// <summary>Primitive types select built-in property drawers rather than a type switch on every redraw.</summary>
    [Fact]
    public void PrimitiveDrawersHaveBuiltInFallbacks()
    {
        var summary = PropertyDrawerRegistry.For(typeof(object));

        Assert.NotSame(summary, PropertyDrawerRegistry.For(typeof(bool)));
        Assert.NotSame(summary, PropertyDrawerRegistry.For(typeof(string)));
        Assert.NotSame(summary, PropertyDrawerRegistry.For(typeof(int)));
        Assert.NotSame(summary, PropertyDrawerRegistry.For(typeof(DayOfWeek)));
        Assert.Same(summary, PropertyDrawerRegistry.For(typeof(DrawerRegistryTests)));
    }
}
