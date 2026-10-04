namespace Turian.Tests;

/// <summary>Checks single-stroke capture for held commands and normal chord capture in the shortcuts UI.</summary>
public sealed class ContinuousShortcutCaptureTests
{
    /// <summary>Held commands capture one stroke immediately; normal bindings retain chord, clear and cancel behavior.</summary>
    [Fact]
    public void RebindingContinuousInputDoesNotArmAChord()
    {
        var shortcuts = new ShortcutService(NullLogger.Instance);
        shortcuts.Add(new KeyBinding("move", KeyboardKey.Up) { IsContinuous = true });
        shortcuts.Add(new KeyBinding("action", KeyboardKey.F2));
        shortcuts.Add(new KeyBinding("chord", new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl), new KeyStroke(KeyboardKey.S)));
        var panel = new ShortcutsPanel(shortcuts, Substitute.For<ICommandCatalog>(), Substitute.For<IPanelAccessor>());
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(800, 500));
        var font = Font.FromFamilyName("sans-serif", 14);
        Frame();
        Begin("move");
        input.IsKeyDown(GKey.LeftControl).Returns(true);
        Press(GKey.K);
        Assert.Equal("Ctrl+K", shortcuts.DisplayFor("move"));
        Assert.False(shortcuts.IsCapturing);
        input.IsKeyDown(GKey.LeftControl).Returns(false);
        Begin("move");
        Press(GKey.Backspace);
        Assert.Empty(shortcuts.DisplayFor("move"));
        Begin("move");
        Press(GKey.Escape);
        Assert.False(shortcuts.IsCapturing);
        Assert.Empty(shortcuts.DisplayFor("move"));
        Begin("action");
        input.IsKeyDown(GKey.LeftControl).Returns(true);
        Press(GKey.K);
        Assert.True(shortcuts.IsCapturing);
        input.IsKeyDown(GKey.LeftControl).Returns(false);
        Press(GKey.S);
        Assert.Equal("Ctrl+K, S", shortcuts.DisplayFor("action"));
        Begin("action");
        Press(GKey.F8);
        Assert.Equal("F8", shortcuts.DisplayFor("action"));

        void Begin(string id)
        {
            var node = Descendants(gui.RootNode!).Single(item => item.Id == "shortcuts/row/" + id + "/chord");
            input.MousePosition.Returns(node.Rect.Center);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Frame();
            input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Frame();
            Assert.True(shortcuts.IsCapturing);
        }
        void Press(GKey key)
        {
            input.IsKeyPressed(key).Returns(true);
            Frame();
            input.IsKeyPressed(key).Returns(false);
            Frame();
        }
        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
    }

    static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));
}
