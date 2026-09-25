namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="UiRenderer"/> — parsing a <c>.ui</c> string and rendering it through
/// Guinevere headlessly (no GPU), asserting on the output bitmap and on side effects.
/// </summary>
public sealed class UiRendererTests
{
    const int w = 200;
    const int h = 120;

    static byte[] Render(string ui, Action<UiRenderer>? configure = null, IInputHandler? input = null)
    {
        var document = UiXmlParser.Parse(ui);
        var renderer = new UiRenderer(document);
        configure?.Invoke(renderer);

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var gui = new Gui { Input = input ?? Substitute.For<IInputHandler>() };
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

    /// <summary>A container's inline <c>background-color</c> is painted.</summary>
    [Fact]
    public void Container_BackgroundColor_IsPainted()
    {
        var px = Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement style="flex-grow: 1; background-color: #ff0000" />
            </UI>
            """);

        var (r, g, b, _) = At(px, w / 2, h / 2);
        Assert.True(r > 200 && g < 60 && b < 60, $"expected red background, got ({r},{g},{b})");
    }

    /// <summary>A <c>&lt;Template&gt;</c> expands with <c>$param</c> substitution.</summary>
    [Fact]
    public void Template_Instance_Expands()
    {
        // Two rows with different background colors proves each instance is stamped independently.
        var px = Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <Template name="Row">
                <VisualElement style="height: 40; background-color: $bg" />
              </Template>
              <VisualElement style="flex-direction: column">
                <Instance template="Row" bg="#00ff00" />
                <Instance template="Row" bg="#0000ff" />
              </VisualElement>
            </UI>
            """);

        Assert.True(At(px, w / 2, 15).G > 200, "first row should be green");
        Assert.True(At(px, w / 2, 60).B > 200, "second row should be blue");
    }

    /// <summary>A missing template renders a visible error marker rather than throwing.</summary>
    [Fact]
    public void MissingTemplate_DoesNotThrow()
    {
        var ex = Record.Exception(() => Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement><Instance template="Nope" /></VisualElement>
            </UI>
            """));

        Assert.Null(ex);
    }

    /// <summary>Clicking a button invokes the wired handler.</summary>
    [Fact]
    public void Button_Click_InvokesHandler()
    {
        var fired = 0;
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(30, 15));           // over a button at the origin
        input.IsMouseButtonPressed(Arg.Any<MouseButton>()).Returns(true); // a click this frame

        Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <Button name="go" text="Go" width="120" height="36" click="OnGo" />
            </UI>
            """,
            r => r.Handlers["OnGo"] = () => fired++,
            input);

        Assert.True(fired > 0, "the click handler should have run");
    }

    /// <summary>
    /// Renders a document over several frames against one <see cref="Gui"/>, so state that lives on
    /// the Gui — focus, the caret — survives between them.
    /// </summary>
    static byte[] RenderFrames(UiRenderer renderer, params IInputHandler[] frames)
    {
        var font = Font.FromFamilyName("sans-serif", 14);
        var gui = new Gui();
        byte[] pixels = [];

        foreach (var input in frames)
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            gui.Input = input;
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
            pixels = [.. pixmap.GetPixelSpan()];
        }

        return pixels;
    }

    static IInputHandler Pointer(float x, float y, bool pressed = false, string typed = "")
    {
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(x, y));
        input.PrevMousePosition.Returns(new Vector2(x, y));
        input.IsMouseButtonPressed(Arg.Any<MouseButton>()).Returns(pressed);
        input.GetTypedCharacters().Returns(typed);
        return input;
    }

    /// <summary>True when any pixel in the frame is close to the given color.</summary>
    static bool Contains(byte[] px, byte r, byte g, byte b)
    {
        for (var i = 0; i + 3 < px.Length; i += 4)
            if (Math.Abs(px[i] - r) < 40 && Math.Abs(px[i + 1] - g) < 40 && Math.Abs(px[i + 2] - b) < 40
                && px[i + 3] > 200)
                return true;

        return false;
    }

    const string tabsDocument = """
        <UI xmlns="https://turian.mass4.org/ui">
          <Tabs name="t">
            <Tab header="One" width="60" height="20">
              <Label text="first" style="color:#ff0000; font-size:14" />
            </Tab>
            <Tab header="Two" width="60" height="20">
              <Label text="second" style="color:#00ff00; font-size:14" />
            </Tab>
          </Tabs>
        </UI>
        """;

    /// <summary>The first tab's body shows until another header is clicked.</summary>
    [Fact]
    public void Tabs_ShowFirstBodyUntilAnotherHeaderIsClicked()
    {
        var renderer = new UiRenderer(UiXmlParser.Parse(tabsDocument));

        var first = RenderFrames(renderer, Pointer(-1, -1));
        Assert.True(Contains(first, 255, 0, 0), "the first tab's body should be showing");
        Assert.False(Contains(first, 0, 255, 0), "the second tab's body should be hidden");
    }

    /// <summary>Clicking the second header swaps which body renders.</summary>
    [Fact]
    public void Tabs_ClickingHeaderSwitchesBody()
    {
        var renderer = new UiRenderer(UiXmlParser.Parse(tabsDocument));

        // Headers are 60px wide and laid out in a row, so the second spans x 60..120.
        var after = RenderFrames(renderer, Pointer(90, 10, pressed: true), Pointer(-1, -1));

        Assert.True(Contains(after, 0, 255, 0), "the second tab's body should be showing");
        Assert.False(Contains(after, 255, 0, 0), "the first tab's body should be hidden");
    }

    sealed class TextModel
    {
        public string Value { get; set; } = "ab";
    }

    /// <summary>
    /// A focused <c>&lt;TextField&gt;</c> routes typing through Guinevere's text editor and writes the
    /// result back through its two-way binding.
    /// </summary>
    [Fact]
    public void TextField_TypingWritesBackThroughBinding()
    {
        var model = new TextModel();
        var renderer = new UiRenderer(UiXmlParser.Parse(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <TextField name="f" width="150" height="30" binding-value="{Value, mode=TwoWay}" />
            </UI>
            """));
        renderer.Bind(model);

        // Focus is granted on the click and observed on the frame after it, so typing comes second.
        RenderFrames(renderer, Pointer(80, 15, pressed: true), Pointer(80, 15, typed: "Z"));

        Assert.Equal("abZ", model.Value);
    }

    /// <summary>A <c>&lt;Repeat&gt;</c> renders one row per item from the data resolver.</summary>
    [Fact]
    public void Repeat_RendersRowPerItem()
    {
        var px = Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement style="flex-direction: column">
                <Repeat items="{Rows}" as="row">
                  <VisualElement style="height: 30; background-color: {row}" />
                </Repeat>
              </VisualElement>
            </UI>
            """,
            r => r.DataResolver = path => path == "Rows" ? new[] { "#ff0000", "#00ff00", "#0000ff" } : null);

        Assert.True(At(px, w / 2, 10).R > 200, "row 0 red");
        Assert.True(At(px, w / 2, 45).G > 200, "row 1 green");
        Assert.True(At(px, w / 2, 75).B > 200, "row 2 blue");
    }

    /// <summary>A leaf <c>&lt;Label&gt;</c> takes its color from a matching <c>.uss</c> class rule.</summary>
    [Fact]
    public void Label_TakesColorFromStylesheetClass()
    {
        var px = Render(
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement style="flex-grow: 1; align-items: center; justify-content: center">
                <Label class="big" text="HELLO" />
              </VisualElement>
            </UI>
            """,
            r => r.StyleSheets.Add(StyleSheet.Parse(".big { color: #ff0000; font-size: 40; }")));

        var reddish = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (rr, gg, bb, aa) = At(px, x, y);
                if (aa > 40 && rr > 150 && gg < 100 && bb < 100) reddish++;
            }

        Assert.True(reddish > 40, $"expected red glyph pixels from the .uss rule, found {reddish}");
    }
}
