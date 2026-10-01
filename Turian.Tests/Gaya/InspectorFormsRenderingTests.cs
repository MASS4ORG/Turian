namespace Turian.Tests;

/// <summary>Exercises composed forms and contextual actions through both headless Inspector passes.</summary>
public sealed class InspectorFormsRenderingTests
{
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
        var model = FormBuilder.Build(target, readOnly: true);
        var calls = 0;
        var section = model.Sections[0] with
        {
            Buttons = [new InspectorButton("Save", () =>
            {
                Assert.Equal(Pass.Pass2Render, gui.Pass);
                calls++;
            }, () => enabled)],
        };
        selection.Select(new FormInspection(target, model with { Sections = [section] }, "registry:studio"));
        using var panel = new InspectorPanel(selection, assets, null!, null!, null!, new InspectorSettings(),
            null!, null!, null!, null!, null!, null!);
        using var surface = SKSurface.Create(new SKImageInfo(640, 800));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame(gui, surface, font, panel.Render);
        var button = Descendants(gui.RootNode!).Single(n => n.Id == "inspector/section0/button0");
        input.MousePosition.Returns(new Vector2(button.Rect.X + 5, button.Rect.Y + 5));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        Frame(gui, surface, font, panel.Render);
        Assert.Equal(enabled ? 1 : 0, calls);
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
