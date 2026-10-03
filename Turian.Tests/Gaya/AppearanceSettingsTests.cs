using Turian.Editor.Studio;

namespace Turian.Tests;

/// <summary>Checks shared appearance preferences and live desktop window gestures.</summary>
public sealed class AppearanceSettingsTests
{
    /// <summary>A plugin that resolves the host's appearance preferences during activation.</summary>
    [Plugin("test.appearance", "Appearance consumer")]
    public sealed class AppearanceConsumer : IPlugin
    {
        /// <summary>The shared preferences resolved at startup.</summary>
        public AppearanceSettings? Appearance { get; private set; }

        /// <inheritdoc />
        public void Configure(IPluginContext context) { }

        /// <inheritdoc />
        public void Start(IServiceProvider services) => Appearance = services.GetRequiredService<AppearanceSettings>();

        /// <inheritdoc />
        public void Stop(IServiceProvider services) { }

        /// <inheritdoc />
        public void Tick(IServiceProvider services, float deltaTime) { }
    }

    /// <summary>A plugin can resolve the same Appearance object that the host's Settings page edits.</summary>
    [Fact]
    public void PluginsReadTheHostAppearancePreferences()
    {
        using var app = PluginHost.Load([typeof(AppearanceConsumer).Assembly], NullLogger.Instance);
        var plugin = Assert.IsType<AppearanceConsumer>(Assert.Single(app.Plugins));
        Assert.Same(app.Services.GetRequiredService<AppearanceSettings>(), plugin.Appearance);
        Assert.Same(app.Settings.Pages.Single(page => page.Id == AppearanceSettings.PageId).Target, plugin.Appearance);
    }

    /// <summary>Studio selects XWayland when available and preserves an explicit backend choice.</summary>
    [Theory]
    [InlineData(true, ":0", null, true)]
    [InlineData(true, ":1", "1", false)]
    [InlineData(true, ":1", "0", false)]
    [InlineData(true, null, null, false)]
    [InlineData(true, "", null, false)]
    [InlineData(false, ":0", null, false)]
    public void DesktopBackendSupportsWindowMovement(bool linux, string? display, string? preference, bool selected)
    {
        var values = new Dictionary<string, string?> { ["DISPLAY"] = display, ["SILKNET_USE_WAYLAND"] = preference };
        var changes = 0;
        WindowPlatform.Configure(linux, key => values.GetValueOrDefault(key), (key, value) =>
        {
            values[key] = value;
            changes++;
        });
        Assert.Equal(selected ? "0" : preference, values["SILKNET_USE_WAYLAND"]);
        Assert.Equal(selected ? 1 : 0, changes);
    }

    /// <summary>Appearance is owned by the host and available to applications without Turian plugins.</summary>
    [Fact]
    public void HostProvidesOneAppearancePage()
    {
        using var app = PluginHost.Load([], NullLogger.Instance);
        var page = app.Settings.Pages.Single(page => page.Id == AppearanceSettings.PageId);
        Assert.Equal("Appearance", page.Path);
        Assert.Same(app.Services.GetRequiredService<AppearanceSettings>(), page.Target);
        Assert.DoesNotContain(app.Settings.Pages, page => page.Id is "gaya.window" or "gaya.turian.appearance");
    }

    /// <summary>Stored font, theme and window preferences move into the shared page and survive another restart.</summary>
    [Fact]
    public void MigratesAppearanceAndWindowPreferences()
    {
        using var frame = new GayaChromeTests.Frame();
        var path = frame.Settings.PathFor(SettingsScope.User);
        File.WriteAllText(path, """
            {"gaya.turian.appearance":{"Theme":"Light","TextSize":16,"Zoom":1.25},
             "gaya.window":{"NativeTitlebar":true}}
            """);
        var restored = new EditorSettings(NullLogger.Instance, path);
        AppearanceSettings.Register(restored, "gaya.turian.appearance");
        using var app = Application(restored);
        using var workbench = new Workbench(app, layoutStore: LayoutStore(path));
        Assert.Equal("Light", workbench.Appearance.Theme);
        Assert.Equal(16, workbench.Appearance.TextSize);
        Assert.Equal(1.25f, workbench.Appearance.Zoom);
        Assert.True(workbench.NativeTitlebar);
        Assert.Equal("Light", app.Themes.CommittedName);
        Assert.Equal(16f, app.Themes.Current.Text(12));
        restored.Save();
        using var nextApp = Application(new EditorSettings(NullLogger.Instance, path));
        using var next = new Workbench(nextApp, layoutStore: LayoutStore(path));
        Assert.Equal("Light", next.Appearance.Theme);
        Assert.Equal(16, next.Appearance.TextSize);
        Assert.True(next.NativeTitlebar);
        next.Appearance.TextSize = 2;
        Assert.Equal(12, next.Appearance.TextSize);
        Assert.Throws<ArgumentNullException>(() => AppearanceSettings.Register(null!));
    }

    /// <summary>The theme dropdown offers custom themes and changes the same committed choice as the menu.</summary>
    [Fact]
    public void ThemeDropdownAndMenuShareThePersistedChoice()
    {
        using var frame = new GayaChromeTests.Frame();
        frame.Themes.Register(StudioTheme.Dark with { Name = "Custom" });
        OpenSettings(frame);
        var editor = Nodes(frame).Single(node => node.Id == $"settings/{AppearanceSettings.PageId}/field0/editor");
        Click(frame, editor.Rect.Center);
        var option = Nodes(frame).Single(node => node.Id.EndsWith("/list/" + (frame.Themes.Themes.Count - 1)));
        Click(frame, option.Rect.Center);
        frame.Draw();
        Assert.Equal("Custom", frame.Themes.CommittedName);
        Assert.Equal("Custom", frame.Workbench.Appearance.Theme);
        frame.Themes.Apply("Light");
        Assert.Equal("Light", frame.Workbench.Appearance.Theme);
        frame.Themes.Preview("Dark");
        Assert.Equal("Light", frame.Workbench.Appearance.Theme);
        frame.Themes.EndFrame();
        frame.Themes.EndFrame();
        frame.Settings.Save();
        using var restoredApp = Application(new EditorSettings(NullLogger.Instance,
            frame.Settings.PathFor(SettingsScope.User)));
        using var restored = new Workbench(restoredApp, layoutStore: LayoutStore(
            frame.Settings.PathFor(SettingsScope.User)));
        Assert.Equal("Light", restoredApp.Themes.CommittedName);
    }

    /// <summary>The live checkbox switches native decorations and application buttons, then the bar moves the window.</summary>
    [Fact]
    public void AppearanceToggleSwitchesDecorationsButtonsAndDragging()
    {
        using var frame = new GayaChromeTests.Frame();
        var window = Substitute.For<IWindowChromeCapability>();
        window.CanMove.Returns(true);
        var position = new Vector2(100, 200);
        window.Position.Returns(_ => position);
        window.When(value => value.Position = Arg.Any<Vector2>()).Do(call => position = call.Arg<Vector2>());
        window.PointerPosition.Returns(_ => position + frame.Input.MousePosition);
        frame.Gui.Platform.Register(window);
        OpenSettings(frame);
        var checkbox = Nodes(frame).Single(node => node.Id == $"settings/{AppearanceSettings.PageId}/field3/editor");
        Click(frame, checkbox.Rect.Position + new Vector2(6, checkbox.Rect.H / 2));
        frame.Draw();
        Assert.True(frame.Workbench.NativeTitlebar);
        Assert.Single(frame.Gui.RootNode!.Children[0].Children[0].Children);
        window.Received().DrawWindowTitlebar(true);
        Click(frame, checkbox.Rect.Position + new Vector2(6, checkbox.Rect.H / 2));
        frame.Draw();
        Assert.False(frame.Workbench.NativeTitlebar);
        Assert.Equal(4, frame.Gui.RootNode!.Children[0].Children[0].Children.Count);
        window.Received().DrawWindowTitlebar(false);
        frame.Input.MousePosition.Returns(new Vector2(400, 18));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Input.IsMouseButtonDown(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Input.MousePosition.Returns(new Vector2(440, 38));
        frame.Draw();
        Assert.Equal(new Vector2(140, 220), position);
    }

    static GayaApplication Application(EditorSettings settings) => new(new ServiceCollection().BuildServiceProvider(),
        new PanelRegistry(), new CommandRegistry(), new MenuRegistry(), new ChromeRegistry(),
        new TabStripChromeRegistry(), new ShortcutService(NullLogger.Instance), new FocusTracker(), [], settings: settings);

    static WorkbenchLayoutStore LayoutStore(string path) => new(NullLogger.Instance, path + ".layout");

    static IEnumerable<LayoutNode> Nodes(GayaChromeTests.Frame frame) =>
        GayaChromeTests.Descendants(frame.Gui.RootNode!);

    static void OpenSettings(GayaChromeTests.Frame frame)
    {
        using var shell = PluginHost.Load([], NullLogger.Instance);
        frame.Panels.Register(shell.Panels.All.Single(panel => panel.Id == ShellPanels.Settings));
        frame.Draw();
        frame.Workbench.ShowPanel(ShellPanels.Settings);
        frame.Draw();
        frame.Draw();
    }

    static void Click(GayaChromeTests.Frame frame, Vector2 pointer)
    {
        frame.Input.MousePosition.Returns(pointer);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
    }
}
