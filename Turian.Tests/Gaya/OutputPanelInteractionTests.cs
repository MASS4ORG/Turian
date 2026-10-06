namespace Turian.Tests;

/// <summary>Drives Output selection, severity toggles and copy actions through headless frames.</summary>
[Collection(SerialTests.Name)]
public sealed class OutputPanelInteractionTests
{
    const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Right click selects an entry and exposes open, copy-line and copy-all actions.</summary>
    [Fact]
    public void ContextMenuUsesTheClickedEvent()
    {
        LogBuffer.Clear();
        try
        {
            using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
            using var services = new ServiceCollection().BuildServiceProvider();
            var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(),
                services, NullLogger.Instance);
            var settings = new OutputPanelSettings { ClearOnRecompile = false };
            var bridge = new OutputLogBridge(settings, play, build, NullLogger.Instance);
            var focus = Substitute.For<IFocusTracker>();
            focus.ActivePanelId.Returns(GayaPlugin.OutputPanelId);
            var panel = new OutputPanel(NullLogger.Instance, settings, bridge, new SettingsService(),
                focus, new StudioLocalization());
            var input = Substitute.For<IInputHandler>();
            input.MousePosition.Returns(new Vector2(-1));
            var clipboard = "";
            input.GetClipboardText().Returns(_ => clipboard);
            input.When(handler => handler.SetClipboardText(Arg.Any<string>()))
                .Do(call => clipboard = call.Arg<string>());
            var gui = new Gui { Input = input };
            using var surface = SKSurface.Create(new SKImageInfo(1200, 600));
            var font = Font.FromFamilyName("sans-serif", 14);
            void Frame()
            {
                gui.Time.Update(0.016);
                InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
                input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
                input.IsMouseButtonPressed(GMouseButton.Right).Returns(false);
                input.IsKeyPressed(GKey.Escape).Returns(false);
            }
            void Click(Vector2 at, GMouseButton button = GMouseButton.Left)
            {
                input.MousePosition.Returns(at);
                input.IsMouseButtonPressed(button).Returns(true);
                input.IsMouseButtonDown(button).Returns(true);
                Frame();
                input.IsMouseButtonDown(button).Returns(false);
                Frame();
            }
            var logger = LogBuffer.Provider.CreateLogger("Game");
            for (var i = 0; i < 2; i++) logger.LogInformation("/missing/Game.cs(7): first\nsecond line");
            logger.LogDebug("/missing/Game.cs(8): debug");
            Frame();
            var row = Nodes(gui.RootNode!).Single(node => node.Id == "gaya.turian.output/row/0");
            Click(row.Center, GMouseButton.Right);
            Assert.Equal(0, typeof(OutputPanel).GetField("selectedIndex", instanceFields)!.GetValue(panel));
            var menu = new FlyoutBuilder();
            typeof(OutputPanel).GetMethod("BuildContextMenu", instanceFields)!.Invoke(panel, [menu, gui]);
            var items = (List<FlyoutItem>)typeof(FlyoutBuilder).GetField("Items", instanceFields)!.GetValue(menu)!;
            Assert.Equal(["Open", "Copy Line", "Copy All"], items.Select(item => item.Text));
            Assert.False(items[0].Enabled);
            items[1].Action!();
            Assert.Equal("/missing/Game.cs(7): first", input.GetClipboardText());
            items[2].Action!();
            Assert.Contains("second line", input.GetClipboardText());
            Assert.Contains("debug", input.GetClipboardText());
            input.IsKeyPressed(GKey.Escape).Returns(true);
            Frame();
            Frame();
            Click(row.Center);
            Click(row.Center);
            Frame();
            Assert.Equal(0, typeof(OutputPanel).GetField("selectedIndex", instanceFields)!.GetValue(panel));
            var header = Nodes(gui.RootNode!).Single(node => node.Id == "gaya.turian.output/header");
            Click(header.Children[3].Center);
            Frame();
            var rows = (IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!.GetValue(panel)!;
            Assert.Equal(LogLevel.Debug, Assert.Single(rows).Line.Level);
            header = Nodes(gui.RootNode!).Single(node => node.Id == "gaya.turian.output/header");
            Click(header.Children[3].Center);
            Click(header.Children[0].Center);
            Frame();
            rows = (IReadOnlyList<LogRow>)typeof(OutputPanel).GetField("rows", instanceFields)!.GetValue(panel)!;
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[0].Count);
        }
        finally { LogBuffer.Clear(); }
    }

    static IEnumerable<LayoutNode> Nodes(LayoutNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Nodes));
}
