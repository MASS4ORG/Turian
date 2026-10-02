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

        Assert.Contains(field.Metadata!.RenderingHints, hint => hint is TooltipAttribute);
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
        if (node.Id is { } id) ids.Add(id);
        foreach (var child in node.Children) Collect(child, ids);
    }
}
