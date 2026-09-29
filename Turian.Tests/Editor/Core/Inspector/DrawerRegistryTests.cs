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

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class Target
    {
        [Marker(1), Outer, Marker(2)]
        public int Value { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class TooltippedTarget
    {
        [Tooltip("Explains the value")]
        public int Value { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class NumericTarget
    {
        public int Value { get; set; } = 42;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class NullableTarget
    {
        public int? Value { get; set; } = 5;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class ReferenceTarget
    {
        public Node? Value { get; set; }
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
        public int DrawCalls { get; private set; }
        public int ValueOnlyCalls { get; private set; }
        public bool ValueOnly { get; init; } = true;
        public void Draw(Gui gui, FormField field, string id) => DrawCalls++;
        public bool DrawValue(Gui gui, FormField field, string id)
        {
            ValueOnlyCalls++;
            return ValueOnly;
        }
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

    /// <summary>Removing an extension restores the built-in tooltip decorator.</summary>
    [Fact]
    public void UnregisteringATooltipOverrideRestoresTheDefault()
    {
        var original = AttributeDrawerRegistry.Find(typeof(TooltipAttribute));
        var custom = new TraceDrawer([], "custom", 10);
        Assert.NotNull(original);
        AttributeDrawerRegistry.Register<TooltipAttribute>(custom);
        try
        {
            Assert.Same(custom, AttributeDrawerRegistry.Find(typeof(TooltipAttribute)));
        }
        finally
        {
            AttributeDrawerRegistry.Unregister<TooltipAttribute>();
        }

        Assert.Same(original, AttributeDrawerRegistry.Find(typeof(TooltipAttribute)));
    }

    /// <summary>A registered drawer wins over the normal reference slot even when a picker exists.</summary>
    [Fact]
    public void RegisteredPropertyDrawerOverridesAReference()
    {
        var field = FormBuilder.Build(new ReferenceTarget()).Sections[0].Fields.Single();
        var custom = new TestPropertyDrawer();
        PropertyDrawerRegistry.Register(typeof(Node), custom);
        try
        {
            FieldDrawers.Draw(null!, field, "reference-test",
                references: new ReferenceDrawer(null!, null!, null!));
            Assert.Equal(1, custom.DrawCalls);
        }
        finally
        {
            PropertyDrawerRegistry.Unregister(typeof(Node));
        }
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
        FieldDrawers.Draw(gui, field, "tooltip-test");
        gui.CalculateLayout();
        var wrapper = Assert.Single(gui.RootNode!.Children,
            node => node.Id == "tooltip-test/tooltip");
        Assert.True(wrapper.Rect is { W: > 0, H: > 0 });
        var row = Assert.Single(wrapper.Children, node => node.Id == "tooltip-test");
        Assert.Equal(wrapper.Rect, row.Rect);
        gui.SetStage(Pass.Pass2Render);
        FieldDrawers.Draw(gui, field, "tooltip-test");
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
        Assert.Same(PropertyDrawerRegistry.For(typeof(int)), PropertyDrawerRegistry.For(typeof(int?)));
        Assert.Same(PropertyDrawerRegistry.For(typeof(bool)), PropertyDrawerRegistry.For(typeof(bool?)));
        Assert.Same(PropertyDrawerRegistry.For(typeof(DayOfWeek)), PropertyDrawerRegistry.For(typeof(DayOfWeek?)));
    }

    /// <summary>A full-row-only drawer leaves the label-free primitive control editable.</summary>
    [Fact]
    public void DecliningValueOnlyRenderingFallsBackToTheBuiltInControl()
    {
        var field = FormBuilder.Build(new NumericTarget()).Sections[0].Fields.Single();
        var expected = RenderEditorOnly(field);
        PropertyDrawerRegistry.Register(typeof(int), new TestPropertyDrawer { ValueOnly = false });
        try
        {
            Assert.Equal(expected, RenderEditorOnly(field));
        }
        finally
        {
            PropertyDrawerRegistry.Unregister(typeof(int));
        }
    }

    /// <summary>An exact nullable registration is used by both inspector and label-free rendering.</summary>
    [Fact]
    public void NullableDrawerRegistrationIsNotLostDuringDispatch()
    {
        var field = FormBuilder.Build(new NullableTarget()).Sections[0].Fields.Single();
        var custom = new TestPropertyDrawer();
        PropertyDrawerRegistry.Register(typeof(int?), custom);
        try
        {
            FieldDrawers.Draw(null!, field, "nullable-field");
            FieldDrawers.DrawEditorOnly(null!, field, "nullable-value");

            Assert.Equal(1, custom.DrawCalls);
            Assert.Equal(1, custom.ValueOnlyCalls);
        }
        finally
        {
            PropertyDrawerRegistry.Unregister(typeof(int?));
        }
    }

    static byte[] RenderEditorOnly(FormField field)
    {
        using var surface = SKSurface.Create(new SKImageInfo(320, 120, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        var font = Font.FromFamilyName("sans-serif", 14);
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        FieldDrawers.DrawEditorOnly(gui, field, "numeric-test");
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        FieldDrawers.DrawEditorOnly(gui, field, "numeric-test");
        gui.Render();
        gui.EndFrame();
        using var snapshot = surface.Snapshot();
        using var pixels = snapshot.PeekPixels();
        return [.. pixels.GetPixelSpan()];
    }
}
