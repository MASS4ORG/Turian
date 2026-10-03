namespace Turian.Tests;

/// <summary>Exercises composed forms and contextual actions through both headless Inspector passes.</summary>
public sealed class InspectorFormsRenderingTests
{
    /// <summary>A settings selection made during rendering takes effect on the next complete frame.</summary>
    [Theory]
    [InlineData(typeof(PlayerSettings))]
    [InlineData(typeof(InputSettings))]
    [InlineData(typeof(GraphicsSettings))]
    public void SettingsSelectionBetweenPassesUsesNextFrame(Type settingsType)
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var picker = new ReferencePicker(null!, null!, null!);
        using var panel = new InspectorPanel(selection, assets, picker, null!, null!, new InspectorSettings(),
            null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        var inspection = new AssetInspection(null!, "settings.dataasset", Activator.CreateInstance(settingsType),
            "Settings", IsPayload: true);

        Frame(gui, surface, font, current =>
        {
            if (current.Pass == Pass.Pass2Render) selection.Select(inspection);
            panel.Render(current);
        });
        Assert.DoesNotContain(Descendants(gui.RootNode!), n => n.Id == "inspector/asset/fields");

        Frame(gui, surface, font, panel.Render);
        Assert.Contains(Descendants(gui.RootNode!), n => n.Id == "inspector/asset/fields");
    }

    /// <summary>Composed actions run only when enabled and clicked during the render pass.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedActionsRespectAvailability(bool enabled)
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var target = new ScopedRegistry { Name = "Studio" };
        var model = InspectorForms.Build(target, readOnly: true);
        var calls = 0;
        var section = model.Sections[0] with
        {
            Buttons = [new Button("Save", () =>
            {
                Assert.Equal(Pass.Pass2Render, gui.Pass);
                calls++;
            }, () => enabled)],
        };
        selection.Select(new FormInspection(target, model with { Sections = [section] }, "registry:studio"));
        using var panel = new InspectorPanel(selection, assets, null!, null!, null!, new InspectorSettings(),
            null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame(gui, surface, font, panel.Render);
        var button = Descendants(gui.RootNode!).Single(n => n.Id == "inspector/section0/button0");
        input.MousePosition.Returns(new Vector2(button.Rect.X + 5, button.Rect.Y + 5));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(enabled ? 1 : 0, calls);
    }

    /// <summary>
    /// A selected node draws through the shared renderer with Turian's drawers: its transform as the transform
    /// editor's vector rows, and each component as its own section with a reference slot for the model.
    /// </summary>
    [Fact]
    public void SelectedNodeDrawsTransformAndComponentThroughTurianDrawers()
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        var node = new Node { Name = "probe" };
        node.Components.Add(new ModelComponent());
        selection.Select(node);
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!), null!, null!,
            new InspectorSettings(), null!, null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);

        Frame(gui, surface, font, panel.Render);
        Frame(gui, surface, font, panel.Render);

        var ids = Descendants(gui.RootNode!).Select(n => n.Id).OfType<string>().ToList();
        Assert.Contains(ids, id => id.EndsWith("/pos", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.EndsWith("/scale", StringComparison.Ordinal));
        Assert.Contains("inspector/section1", ids);
        Assert.Contains(ids, id => id.StartsWith("inspector/section1/", StringComparison.Ordinal)
                                   && id.EndsWith("/ref", StringComparison.Ordinal));
    }

    internal static void Frame(Gui gui, SKSurface surface, Font font, Action<Gui> render)
    {
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        render(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        render(gui);
        gui.Render();
        gui.EndFrame();
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
