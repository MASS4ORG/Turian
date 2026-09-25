namespace Turian.Tests;

/// <summary>Tests for the <c>.ui</c> data-binding engine: <see cref="MemberPath"/>,
/// <see cref="ValueConverters"/>, one-/two-way bindings and <see cref="UiController"/>.</summary>
public sealed class UiBindingTests
{
    const int w = 160;
    const int h = 100;

    // ── member path ──────────────────────────────────────────────────────────

    sealed class Machine
    {
        public string Name { get; set; } = "atom-01";
        public Sub Cpu { get; } = new();
        public bool Verbose { get; set; }
    }

    sealed class Sub
    {
        public double Load { get; set; } = 0.42;
    }

    /// <summary>Paths read nested properties and fall back to the object itself for an empty path.</summary>
    [Fact]
    public void MemberPath_ReadsNested()
    {
        var m = new Machine();
        Assert.Equal("atom-01", MemberPath.Read(m, "Name"));
        Assert.Equal(0.42, MemberPath.Read(m, "Cpu.Load"));
        Assert.Null(MemberPath.Read(m, "Cpu.Missing"));
        Assert.Same(m, MemberPath.Read(m, ""));
    }

    /// <summary>Dictionary roots read and write through dotted member paths.</summary>
    [Fact]
    public void MemberPath_ReadsAndWritesDictionaries()
    {
        var root = new Dictionary<string, object?>
        {
            ["Machine"] = new Dictionary<string, object?> { ["Name"] = "atom-01" },
        };

        Assert.Equal("atom-01", MemberPath.Read(root, "Machine.Name"));
        Assert.Null(MemberPath.Read(root, "Machine.Missing"));

        Assert.True(MemberPath.TryWrite(root, "Machine.Name", "renamed"));
        Assert.Equal("renamed", MemberPath.Read(root, "Machine.Name"));
    }

    /// <summary>Writing coerces string values into the target member type and fails on unknown paths.</summary>
    [Fact]
    public void MemberPath_WritesWithCoercion()
    {
        var m = new Machine();

        Assert.True(MemberPath.TryWrite(m, "Name", "renamed"));
        Assert.Equal("renamed", m.Name);

        Assert.True(MemberPath.TryWrite(m, "Verbose", "true")); // string → bool
        Assert.True(m.Verbose);
        m.Verbose = false;
        Assert.False(m.Verbose);

        Assert.True(MemberPath.TryWrite(m, "Cpu.Load", "0.9")); // nested, string → double
        Assert.Equal(0.9, m.Cpu.Load, 3);

        Assert.False(MemberPath.TryWrite(m, "Nope", 1));
    }

    // ── converters ───────────────────────────────────────────────────────────

    /// <summary>Built-in converters cover boolean, percentage, thousands, upper and lower.</summary>
    [Fact]
    public void Converters_Builtins()
    {
        Assert.Equal(false, ValueConverters.Get("not")!.Convert(true));
        Assert.Equal("42%", ValueConverters.Get("percent")!.Convert(0.42));
        Assert.Equal("1,234,567", ValueConverters.Get("thousands")!.Convert(1234567));
        Assert.Equal("HI", ValueConverters.Get("upper")!.Convert("hi"));
        Assert.Equal("hi", ValueConverters.Get("lower")!.Convert("HI"));
    }

    // ── rendering ────────────────────────────────────────────────────────────

    static byte[] RenderFrame(UiRenderer renderer, IInputHandler input)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var gui = new Gui { Input = input };
        var font = Font.FromFamilyName("sans-serif", 14);
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(canvas, font, font);
        renderer.Render(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        renderer.Render(gui);
        gui.Render();
        gui.EndFrame();
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var pixmap = snapshot.PeekPixels();
        return [.. pixmap.GetPixelSpan()];
    }

    static (byte R, byte G, byte B, byte A) At(byte[] px, int x, int y)
    {
        var i = ((y * w) + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    static IInputHandler Mouse(float x, float y, bool pressed = false)
    {
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(x, y));
        input.PrevMousePosition.Returns(new Vector2(x, y));
        input.IsMouseButtonPressed(Arg.Any<MouseButton>()).Returns(pressed);
        return input;
    }

    sealed class ColorModel
    {
        public string Panel { get; set; } = "#ff0000";
    }

    /// <summary>A one-way binding on a style value updates when the source changes.</summary>
    [Fact]
    public void OneWay_StyleBinding_TracksSource()
    {
        var model = new ColorModel();
        Assert.Equal("#ff0000", model.Panel);
        var doc = UiXmlParser.Parse(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement style="flex-grow: 1; background-color: {Panel}" />
            </UI>
            """);
        var renderer = new UiRenderer(doc);
        renderer.Bind(model);

        var red = RenderFrame(renderer, Mouse(-1, -1));
        Assert.True(At(red, w / 2, h / 2).R > 200);

        model.Panel = "#0000ff";
        var blue = RenderFrame(renderer, Mouse(-1, -1));
        Assert.True(At(blue, w / 2, h / 2).B > 200);
    }

    sealed class ToggleModel
    {
        public bool On { get; set; }
    }

    /// <summary>A two-way <c>&lt;Toggle&gt;</c> binding writes back to the source when toggled.</summary>
    [Fact]
    public void TwoWay_Toggle_WritesBack()
    {
        var model = new ToggleModel { On = false };
        var doc = UiXmlParser.Parse(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <Toggle name="v" binding-value="{On, mode=TwoWay}" />
            </UI>
            """);
        var renderer = new UiRenderer(doc);
        renderer.Bind(model);

        // Toggle laid out at the origin; click its centre.
        RenderFrame(renderer, Mouse(20, 10, pressed: true));

        Assert.True(model.On, "clicking the toggle should have written On = true back to the model");
    }

    sealed class MenuController : UiController
    {
        public int Plays { get; private set; }

        public void OnPlay() => Plays++;
    }

    /// <summary>A controller's public method is wired as an event handler and its properties bind.</summary>
    [Fact]
    public void Controller_EventAndData()
    {
        var controller = new MenuController();
        var onPlay = nameof(MenuController.OnPlay);
        var doc = UiXmlParser.Parse(
            $$"""
            <UI xmlns="https://turian.mass4.org/ui" controller="MenuController">
              <Button name="go" text="Go" width="120" height="36" click="{{onPlay}}" />
            </UI>
            """);
        var renderer = new UiRenderer(doc);
        renderer.Bind(controller: controller);

        RenderFrame(renderer, Mouse(30, 15, pressed: true));

        Assert.Equal(1, controller.Plays);
    }

    /// <summary>Entries in a <c>&lt;Bindings&gt;</c> block are folded onto their target element.</summary>
    [Fact]
    public void ExplicitBindings_AreFolded()
    {
        var model = new ColorModel { Panel = "#00ff00" };
        var doc = UiXmlParser.Parse(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement name="box" style="flex-grow: 1">
                <Bindings>
                  <Binding element="box" property="background-color" path="Panel" />
                </Bindings>
              </VisualElement>
            </UI>
            """);
        var renderer = new UiRenderer(doc);
        renderer.Bind(model);

        var px = RenderFrame(renderer, Mouse(-1, -1));
        Assert.True(At(px, w / 2, h / 2).G > 200, "the folded binding should paint the box green");
    }
}
