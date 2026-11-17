using EngineColor = Turian.Engine.Core.Color;

namespace Turian.Tests.Editor;

/// <summary>
/// Covers Turian's rendering rules over the shared form renderer: references, the transform editor and which
/// objects are edited inline. Generic registry and dispatch behaviour is tested with the renderer itself.
/// </summary>
public class DrawerRegistryTests
{
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class TooltippedTarget
    {
        [Tooltip("Explains the value")]
        public int Value { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class ReferenceTarget
    {
        public Node? Value { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class TransformTarget
    {
        public Transform Value { get; set; } = new();
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class ColorTarget
    {
        public EngineColor Linear { get; set; } = new(1f, 0.5f, 0.25f, 0.5f);

        public Color32 Packed { get; set; } = new(10, 20, 30, 255);

        [ColorUsage]
        public Vector4 Tint { get; set; } = new(1f, 0.5f, 0f, 1f);

        [ColorUsage]
        public Vector3 Sky { get; set; } = new(0.1f, 0.2f, 0.3f);

        public Vector4 Plain { get; set; }
    }

    sealed class TestPropertyDrawer : IPropertyDrawer
    {
        public int DrawCalls { get; private set; }

        public void Draw(Gui gui, FormField field, string id, FormRenderContext context) => DrawCalls++;

        public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context) => false;
    }

    /// <summary>The tooltip reaches the drawer pipeline as rendering metadata.</summary>
    [Fact]
    public void TooltipIsAvailableToTheDrawerPipeline()
    {
        var field = InspectorForms.Build(new TooltippedTarget()).Sections[0].Fields.Single();

        Assert.Contains(field.Metadata!.RenderingHints, static hint => hint is TooltipAttribute);
        Assert.Equal("Explains the value", field.Attribute<TooltipAttribute>()?.Text);
    }

    /// <summary>A drawer registered for a referenced type wins over the reference slot, as a Unity drawer would.</summary>
    [Fact]
    public void RegisteredTypeDrawerOverridesTheReferenceSlot()
    {
        var field = InspectorForms.Build(new ReferenceTarget()).Sections[0].Fields.Single();
        var custom = new TestPropertyDrawer();
        var drawers = new FormDrawers(TurianForms.Drawers);
        using var references = drawers.Add(ReferenceDrawer.Handles, new ReferenceDrawer(null!, null!, null!));
        using var registration = drawers.Add(typeof(Node), custom);

        RenderBothPasses(gui => gui.FormField(field, "reference", new FormRenderContext { Drawers = drawers }));

        Assert.Equal(2, custom.DrawCalls);
    }

    /// <summary>A transform draws through Turian's editor: one vector row each for position, rotation and scale.</summary>
    [Fact]
    public void TransformDrawsThroughTheTurianEditor()
    {
        var field = InspectorForms.Build(new TransformTarget()).Sections[0].Fields.Single();
        var context = new FormRenderContext { Drawers = TurianForms.Drawers, CanInline = TurianForms.CanInline };

        var ids = RenderBothPasses(gui => gui.FormField(field, "transform", context));

        Assert.Contains("transform/pos", ids);
        Assert.Contains("transform/rot", ids);
        Assert.Contains("transform/scale", ids);
    }

    /// <summary>Both engine color types draw as Guinevere's color field instead of four loose channel rows.</summary>
    [Fact]
    public void EngineColorsDrawAsColorFields()
    {
        var model = InspectorForms.Build(new ColorTarget());
        var context = new FormRenderContext { Drawers = TurianForms.Drawers, CanInline = TurianForms.CanInline };

        var ids = RenderBothPasses(gui =>
        {
            foreach (var field in model.Sections[0].Fields)
            {
                gui.FormField(field, field.Name, context);
            }
        });

        Assert.Contains("Linear/color", ids);
        Assert.Contains("Packed/color", ids);
        Assert.Contains("Tint/color", ids);
        Assert.Contains("Sky/color", ids);
        Assert.DoesNotContain("Plain/color", ids);
        Assert.Contains("Plain/a3", ids);
    }

    /// <summary>A color-marked vector shows its 0..1 channels as bytes and writes them back unchanged.</summary>
    [Fact]
    public void ColorVectorsRoundTripThroughThePicker()
    {
        var picked = EngineColorDrawer.ToPicker(new Vector4(1f, 0.5f, 0f, 0.2f));

        Assert.Equal((51, 255, 128, 0), (picked.A, picked.R, picked.G, picked.B));
        Assert.Equal(new Vector4(1f, 128 / 255f, 0f, 51 / 255f),
            EngineColorDrawer.FromPicker(picked, typeof(Vector4)));
        Assert.Equal(new Vector3(1f, 128 / 255f, 0f), EngineColorDrawer.FromPicker(picked, typeof(Vector3)));
    }

    /// <summary>A linear color is shown in sRGB and written back as the same linear value after a picked change.</summary>
    [Fact]
    public void LinearColorsRoundTripThroughTheSrgbPicker()
    {
        var linear = new EngineColor(0.2f, 0.4f, 0.6f, 0.5f);

        var picked = EngineColorDrawer.ToPicker(linear);
        var written = (EngineColor)EngineColorDrawer.FromPicker(picked, typeof(EngineColor));

        Assert.Equal(linear.ToSrgb(), new Color32(picked.R, picked.G, picked.B, picked.A));
        Assert.Equal(linear.R, written.R, 2);
        Assert.Equal(linear.G, written.G, 2);
        Assert.Equal(linear.B, written.B, 2);
        Assert.Equal(linear.A, written.A, 2);
    }

    /// <summary>A packed color passes through the picker unchanged.</summary>
    [Fact]
    public void PackedColorsRoundTripThroughThePicker()
    {
        var packed = new Color32(1, 2, 3, 4);

        Assert.Equal(packed, EngineColorDrawer.FromPicker(EngineColorDrawer.ToPicker(packed), typeof(Color32)));
    }

    /// <summary>Nodes, components and assets are referenced, never expanded inside another object's form.</summary>
    [Fact]
    public void EngineObjectsAreNeverInlined()
    {
        Assert.False(TurianForms.CanInline(typeof(Node)));
        Assert.False(TurianForms.CanInline(typeof(ModelComponent)));
        Assert.False(TurianForms.CanInline(typeof(Asset)));
        Assert.True(TurianForms.CanInline(typeof(TooltippedTarget)));
    }

    /// <summary>Builds and renders one frame in both passes, returning every node id of the render pass.</summary>
    static HashSet<string> RenderBothPasses(Action<Gui> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(480, 200));
        var gui = new Gui { Input = Substitute.For<IInputHandler>() };
        var font = Font.FromFamilyName("sans-serif", 14);
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        draw(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        draw(gui);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Collect(gui.RootNode!, ids);
        gui.Render();
        gui.EndFrame();
        return ids;
    }

    static void Collect(LayoutNode node, HashSet<string> ids)
    {
        if (node.Id is { } id)
        {
            ids.Add(id);
        }

        foreach (var child in node.Children)
        {
            Collect(child, ids);
        }
    }
}
