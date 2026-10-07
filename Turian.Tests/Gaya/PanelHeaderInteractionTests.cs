namespace Turian.Tests;

/// <summary>Drives panel-owned header widgets and settings without a desktop window.</summary>
[Collection(SerialTests.Name)]
public sealed class PanelHeaderInteractionTests
{
    sealed class HeaderFrame : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(800, 400));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly ServiceProvider services;
        readonly IPanel panel;
        readonly string id;

        internal readonly IInputHandler Input = Substitute.For<IInputHandler>();
        internal readonly IEditorSettings Settings = Substitute.For<IEditorSettings>();
        internal readonly Gui Gui;

        internal HeaderFrame(IPanel panel, string id)
        {
            this.panel = panel;
            this.id = id;
            services = new ServiceCollection().AddSingleton(Settings).BuildServiceProvider();
            Input.MousePosition.Returns(new Vector2(-1));
            Gui = new Gui { Input = Input };
            Draw();
            Draw();
        }

        internal void Draw() => InspectorFormsRenderingTests.Frame(Gui, surface, font, gui =>
        {
            using (gui.Node(-1, 24, "panel/header").ExpandWidth().Direction(Axis.Horizontal).Enter())
                panel.RenderHeader(gui, new PanelHeaderContext(id, id, gui.CurrentNode.Rect, false, services));
        });

        internal LayoutNode Find(string nodeId) => Nodes(Gui.RootNode!).Single(node => node.Id == nodeId);

        internal void Click(string nodeId)
        {
            var rectangle = Find(nodeId).Rect;
            Input.MousePosition.Returns(new Vector2(rectangle.X + 6, rectangle.Y + rectangle.H / 2));
            Draw();
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
            Draw();
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Input.IsMouseButtonDown(GMouseButton.Left).Returns(false);
            Draw();
            Draw();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            services.Dispose();
            surface.Dispose();
        }

        static IEnumerable<LayoutNode> Nodes(LayoutNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Nodes));
    }

    /// <summary>The Inspector owns its lock state and preference edits independently of other instances.</summary>
    [Fact]
    public void InspectorLockAndSettingsBelongToThePanel()
    {
        var assets = new AssetManager();
        var selection = new NodeInspectorController(assets);
        selection.Select(new Node());
        var preferences = new InspectorSettings();
        using var panel = new InspectorPanel(selection, assets, new ReferencePicker(null!, null!, null!), null!,
            null!, preferences, null!, null!, null!, null!, null!, null!, new AssetDatabase());
        using var frame = new HeaderFrame(panel, "inspector.instance");
        frame.Click("inspector.instance/lock");
        Assert.True(panel.Locked);
        frame.Click("inspector.instance/lock");
        Assert.False(panel.Locked);
        frame.Click("inspector.instance/settings");
        var previous = preferences.AutoExpandComponents;
        frame.Click("inspector/preferences");
        Assert.Equal(!previous, preferences.AutoExpandComponents);
        frame.Settings.Received().NotifyChanged("gaya.turian.inspector");
    }

    /// <summary>The Output header's menu edits the same settings object the panel consumes.</summary>
    [Fact]
    public void OutputHeaderEditsAndPersistsDisplayPreferences()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var play = new PlayModeService(Substitute.For<IPlaySceneHost>(), new AssetDatabase(),
            services, NullLogger.Instance);
        var preferences = new OutputPanelSettings();
        var panel = new OutputPanel(NullLogger.Instance, preferences,
            new OutputLogBridge(preferences, play, build, NullLogger.Instance), new SettingsService(),
            Substitute.For<IFocusTracker>(), new StudioLocalization());
        using var frame = new HeaderFrame(panel, GayaPlugin.OutputPanelId);
        frame.Click("gaya.turian.output/tabMenu");
        var previous = preferences.ShowTimestamp;
        frame.Click("output/preferences/showTimestamp");
        Assert.Equal(!previous, preferences.ShowTimestamp);
        var toggles = (preferences.Monospace, preferences.ClearOnPlay, preferences.ClearOnBuild,
            preferences.ClearOnRecompile);
        frame.Click("output/preferences/monospace");
        frame.Click("output/preferences/clearOnPlay");
        frame.Click("output/preferences/clearOnBuild");
        frame.Click("output/preferences/clearOnRecompile");
        Assert.Equal(!toggles.Monospace, preferences.Monospace);
        Assert.Equal(!toggles.ClearOnPlay, preferences.ClearOnPlay);
        Assert.Equal(!toggles.ClearOnBuild, preferences.ClearOnBuild);
        Assert.Equal(!toggles.ClearOnRecompile, preferences.ClearOnRecompile);
        var lines = preferences.EntryLines;
        frame.Click("gaya.turian.output/tabMenu/plus");
        Assert.Equal(lines + 1, preferences.EntryLines);
        frame.Click("gaya.turian.output/tabMenu/minus");
        Assert.Equal(lines, preferences.EntryLines);
        frame.Settings.Received().NotifyChanged(OutputPanelSettings.PageId);
    }
}
