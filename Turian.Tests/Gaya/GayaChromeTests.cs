namespace Turian.Tests;

/// <summary>Checks application bar composition and the lifetime of plugin chrome across both GUI passes.</summary>
[Collection(SerialTests.Name)]
public sealed class GayaChromeTests
{
    sealed class Item(Action<Gui>? render = null, Action? dispose = null) : IChromeItem, IDisposable
    {
        /// <summary>Counts invocations across the build and render passes.</summary>
        public int Renders { get; private set; }
        /// <summary>Counts resource releases.</summary>
        public int Disposals { get; private set; }

        /// <inheritdoc />
        public void Render(Gui gui)
        {
            Renders++;
            render?.Invoke(gui);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Disposals++;
            dispose?.Invoke();
        }
    }

    internal sealed class Frame : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), $"gaya-chrome-{Guid.NewGuid():N}");
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(800, 600));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly GayaApplication application;

        /// <summary>Creates an isolated workbench with temporary settings and layout files.</summary>
        public Frame(float? zoom = null, IWindowIdentity? identity = null)
        {
            Directory.CreateDirectory(directory);
            var settings = new EditorSettings(NullLogger.Instance, Path.Combine(directory, "settings.json"));
            var themes = new ThemeService();
            var registrations = new ServiceCollection().AddSingleton<ILogger>(NullLogger.Instance)
                .AddSingleton<IEditorSettings>(settings).AddSingleton<IThemeService>(themes)
                .AddSingleton(Blocker);
            if (identity is not null) registrations.AddSingleton(identity);
            var services = registrations.BuildServiceProvider();
            application = new GayaApplication(services, new PanelRegistry(), new CommandRegistry(),
                new MenuRegistry(), Chrome, new ShortcutService(NullLogger.Instance),
                new FocusTracker(), [], settings: settings, themes: themes);
            Workbench = new Workbench(application, null,
                new WorkbenchLayoutStore(NullLogger.Instance, Path.Combine(directory, "layout.json")));
            if (zoom is { } scale) application.Themes.SetScale(ThemeTokens.Default.FontSize, scale);
            Input.MousePosition.Returns(new Vector2(-1, -1));
            Input.PrevMousePosition.Returns(new Vector2(-1, -1));
            Gui.Input = Input;
        }

        /// <summary>The contributions rendered by the workbench.</summary>
        public ChromeRegistry Chrome { get; } = new();
        /// <summary>The input state supplied to both passes.</summary>
        public IInputHandler Input { get; } = Substitute.For<IInputHandler>();
        /// <summary>The persistent GUI context for the headless frames.</summary>
        public Gui Gui { get; } = new();
        /// <summary>The shell under test.</summary>
        public Workbench Workbench { get; }
        /// <summary>The service controlling the modal input blocker.</summary>
        public IUiBlocker Blocker { get; } = Substitute.For<IUiBlocker>();
        /// <summary>The commands available to menus and the command palette.</summary>
        public CommandRegistry Commands => application.Commands;
        /// <summary>The registered menu entries.</summary>
        public MenuRegistry Menus => application.Menus;
        /// <summary>The page registry and persistence used by the workbench.</summary>
        public EditorSettings Settings => application.Settings;
        /// <summary>The services used by contributed panels.</summary>
        public IServiceProvider Services => application.Services;
        /// <summary>The themes available to menus and the Appearance settings page.</summary>
        public ThemeService Themes => application.Themes;
        /// <summary>The panels rendered by the workbench.</summary>
        public PanelRegistry Panels => application.Panels;

        /// <summary>Draws both passes, optionally mutating contributions after layout is calculated.</summary>
        public void Draw(Action? betweenPasses = null)
        {
            Gui.Time.Update(0.016);
            Gui.SetStage(Pass.Pass1Build);
            Gui.BeginFrame(surface.Canvas, font, font);
            Workbench.Render(Gui);
            Gui.CalculateLayout();
            betweenPasses?.Invoke();
            Gui.SetStage(Pass.Pass2Render);
            Workbench.Render(Gui);
            Gui.Render();
            Gui.EndFrame();
        }

        /// <summary>Writes the workbench's offscreen surface for visual inspection.</summary>
        public void Capture(string path)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
        }

        /// <summary>Releases the host and drawing surface and deletes its temporary files.</summary>
        public void Dispose()
        {
            Workbench.Dispose();
            application.Dispose();
            surface.Dispose();
            Directory.Delete(directory, true);
        }
    }

    static ChromeDescriptor Descriptor(string id, ChromeSlot slot = ChromeSlot.MenuBar, int order = 0) =>
        new(id, slot, _ => new Item(), Order: order);

    /// <summary>Slot snapshots stay cached until registrations change and preserve ties in registration order.</summary>
    [Fact]
    public void RegistryCachesOrderedSnapshotsUntilMutation()
    {
        var registry = new ChromeRegistry();
        var last = Descriptor("last", order: 2);
        var first = Descriptor("first", order: 1);
        var tied = Descriptor("tied", order: 1);
        registry.Register(last);
        registry.Register(first);
        registry.Register(tied);
        registry.Register(Descriptor("status", ChromeSlot.StatusBar));

        var snapshot = registry.For(ChromeSlot.MenuBar);
        Assert.Equal([first, tied, last], snapshot);
        Assert.Same(snapshot, registry.For(ChromeSlot.MenuBar));
        Assert.Throws<InvalidOperationException>(() => registry.Register(first));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentNullException>(() => registry.Replace(null!));
        Assert.Throws<KeyNotFoundException>(() => registry.Replace(Descriptor("missing")));
        Assert.Equal(4, registry.Revision);

        registry.Remove("missing");
        Assert.Same(snapshot, registry.For(ChromeSlot.MenuBar));
        var replacement = tied with { Height = 40 };
        registry.Replace(replacement);
        Assert.Equal([first, replacement, last], registry.For(ChromeSlot.MenuBar));
        Assert.Equal([first, tied, last], snapshot);
        registry.Remove("first");
        Assert.Equal([replacement, last], registry.For(ChromeSlot.MenuBar));
        Assert.Equal(6, registry.Revision);
    }

    /// <summary>Chrome in every host region is created once and reused for both passes and subsequent frames.</summary>
    [Fact]
    public void ChromeFactoriesAreCachedAcrossPassesAndFrames()
    {
        using var frame = new Frame();
        var items = new List<Item>();
        var creations = 0;
        foreach (var slot in Enum.GetValues<ChromeSlot>())
        {
            var item = new Item();
            items.Add(item);
            frame.Chrome.Register(new ChromeDescriptor(slot.ToString(), slot, _ =>
            {
                creations++;
                return item;
            }, Height: 40));
        }

        frame.Draw();
        frame.Draw();
        Assert.Equal(items.Count, creations);
        Assert.All(items, item => Assert.Equal(4, item.Renders));
        Assert.All(items, item => Assert.Equal(0, item.Disposals));
    }

    /// <summary>Replacement changes the slot and disposes the old cached instance before drawing the next frame.</summary>
    [Fact]
    public void ReplacingAndRemovingChromeReleaseCachedInstances()
    {
        using var frame = new Frame();
        var old = new Item();
        var next = new Item();
        frame.Chrome.Register(new ChromeDescriptor("item", ChromeSlot.MenuBar, _ => old));
        frame.Draw();
        frame.Chrome.Replace(new ChromeDescriptor("item", ChromeSlot.Overlay, _ => next));
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(2, old.Renders);
        Assert.Equal(2, next.Renders);

        frame.Chrome.Remove("item");
        frame.Draw();
        Assert.Equal(1, next.Disposals);
        Assert.Equal(2, next.Renders);
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(1, next.Disposals);
    }

    /// <summary>Explicit replacement and re-registration invalidate the instance even when the descriptor is reused.</summary>
    [Fact]
    public void ReusingADescriptorStillRecreatesItsChromeInstance()
    {
        using var frame = new Frame();
        var items = new List<Item>();
        var descriptor = new ChromeDescriptor("item", ChromeSlot.MenuBar, _ =>
        {
            var item = new Item();
            items.Add(item);
            return item;
        });
        frame.Chrome.Register(descriptor);
        frame.Draw();
        frame.Chrome.Replace(descriptor);
        frame.Draw();
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Disposals);

        frame.Chrome.Remove("item");
        frame.Chrome.Register(descriptor);
        frame.Draw();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[1].Disposals);
        Assert.Equal(0, items[2].Disposals);
    }

    /// <summary>Replacement between the two passes takes effect at the start of the next frame.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutationsBetweenPassesDoNotChangeTheRenderedTree(bool replace)
    {
        using var frame = new Frame();
        var old = new Item();
        var next = new Item();
        frame.Chrome.Register(new ChromeDescriptor("item", ChromeSlot.MenuBar, _ => old));
        frame.Draw(() =>
        {
            if (replace) frame.Chrome.Replace(new ChromeDescriptor("item", ChromeSlot.Overlay, _ => next));
            else frame.Chrome.Remove("item");
            frame.Chrome.Register(new ChromeDescriptor("added", ChromeSlot.Toolbar, _ => new Item()));
        });
        Assert.Equal(2, old.Renders);
        Assert.Equal(0, old.Disposals);
        Assert.Equal(0, next.Renders);
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(replace ? 2 : 0, next.Renders);
    }

    /// <summary>A replacement made during chrome rendering cannot alter later slots in the same frame.</summary>
    [Fact]
    public void RenderingMutationsAreDeferredUntilTheNextBuild()
    {
        using var frame = new Frame();
        var old = new Item();
        var next = new Item();
        frame.Chrome.Register(new ChromeDescriptor("target", ChromeSlot.StatusBar, _ => old));
        frame.Chrome.Register(new ChromeDescriptor("mutator", ChromeSlot.MenuBar, _ => new Item(gui =>
        {
            if (gui.Pass == Pass.Pass1Build)
                frame.Chrome.Replace(new ChromeDescriptor("target", ChromeSlot.StatusBar, _ => next));
        })));
        frame.Draw();
        Assert.Equal(2, old.Renders);
        Assert.Equal(0, next.Renders);
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(2, next.Renders);
    }

    /// <summary>Contributions registered while another item is being disposed are picked up on the next frame.</summary>
    [Fact]
    public void DisposalMutationsDoNotLoseRegistrations()
    {
        using var frame = new Frame();
        var added = new Item();
        var old = new Item(dispose: () => frame.Chrome.Register(
            new ChromeDescriptor("added", ChromeSlot.MenuBar, _ => added)));
        frame.Chrome.Register(new ChromeDescriptor("old", ChromeSlot.MenuBar, _ => old));
        frame.Draw();
        frame.Chrome.Remove("old");
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(0, added.Renders);
        frame.Draw();
        Assert.Equal(2, added.Renders);
    }

    /// <summary>A chrome factory failure is contained and its fallback is cached until explicitly replaced.</summary>
    [Fact]
    public void FailedFactoryCanBeReplacedWithoutRetryingEveryPass()
    {
        using var frame = new Frame();
        var attempts = 0;
        frame.Chrome.Register(new ChromeDescriptor("broken", ChromeSlot.MenuBar, _ =>
        {
            attempts++;
            throw new InvalidOperationException("Factory failed");
        }));
        frame.Draw();
        frame.Draw();
        Assert.Equal(1, attempts);
        frame.Chrome.Remove("broken");
        frame.Draw();
    }

    /// <summary>Small themes respect AppBar's minimum height and headless rendering needs no window capability.</summary>
    [Fact]
    public void CompactMenuAndOverlaysDoNotReserveExtraBarWidth()
    {
        using var frame = new Frame(zoom: 0.6f);
        frame.Chrome.Register(Descriptor("overlay", ChromeSlot.Overlay));
        frame.Draw();
        var nodes = Descendants(frame.Gui.RootNode!).ToList();
        var menu = Assert.Single(nodes, node => node.Id == "menubar");
        Assert.Equal(24f, menu.Rect.H);
        Assert.Equal(24f, menu.Rect.W);
        var overlay = Assert.Single(nodes, node => node.Id == "chrome/Overlay/overlay");
        Assert.Contains(overlay, frame.Gui.RootNode!.Children);
        Assert.DoesNotContain(Descendants(menu), node => node.Id == overlay.Id);
    }

    /// <summary>The modal blocker covers chrome and dock panels even while the command palette is visible.</summary>
    [Fact]
    public void ModalBlockerCoversTheWorkbenchAndCommandPalette()
    {
        using var frame = new Frame();
        frame.Commands.Register(new CommandDescriptor("test", "Test", _ => { }));
        frame.Workbench.ToggleCommandPalette();
        frame.Blocker.IsBlocked.Returns(true);
        frame.Blocker.Message.Returns("Working");
        frame.Draw();
        var blocker = Descendants(frame.Gui.RootNode!).Single(node => node.Id == "__uiBlocker");
        Assert.True(blocker.Rect.W >= 800);
        Assert.True(blocker.Rect.H >= 600);
        Assert.Throws<ArgumentNullException>(() => frame.Workbench.Render(null!));
    }

    /// <summary>Native decorations and unsupported movement both suppress application-drawn window buttons.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void AppBarOwnsNativeDecorations(bool canMove, bool native, bool expected)
    {
        using var frame = new Frame();
        var window = Substitute.For<IWindowChromeCapability>();
        window.CanMove.Returns(canMove);
        frame.Gui.Platform.Register(window);
        frame.Workbench.NativeTitlebar = native;
        frame.Draw();
        frame.Draw();
        var bar = frame.Gui.RootNode!.Children[0].Children[0];
        Assert.Equal(expected ? 1 : 4, bar.Children.Count);
        if (!expected) window.Received(1).DrawWindowTitlebar(false);
        else window.DidNotReceive().DrawWindowTitlebar(false);
        frame.Workbench.NativeTitlebar = true;
        frame.Draw();
        Assert.Single(frame.Gui.RootNode!.Children[0].Children[0].Children);
        if (!expected) window.Received(1).DrawWindowTitlebar(true);
    }

    /// <summary>A persisted window page is visible in the settings API and changing it takes effect next frame.</summary>
    [Fact]
    public void WindowPreferencesArePersistedAndKeepBothPassesConsistent()
    {
        using var frame = new Frame();
        var page = frame.Settings.Pages.Single(page => page.Id == AppearanceSettings.PageId);
        Assert.Equal("Appearance", page.Path);
        Assert.Equal(SettingsScope.User, page.Scope);
        Assert.False(page.Hidden);
        Assert.Same(frame.Workbench.Appearance, page.Target);
        var window = Substitute.For<IWindowChromeCapability>();
        window.CanMove.Returns(true);
        frame.Gui.Platform.Register(window);
        frame.Draw(() => frame.Workbench.NativeTitlebar = true);
        Assert.Equal(4, frame.Gui.RootNode!.Children[0].Children[0].Children.Count);
        frame.Draw();
        Assert.Single(frame.Gui.RootNode!.Children[0].Children[0].Children);
        frame.Settings.Save();
        var restored = new EditorSettings(NullLogger.Instance, frame.Settings.PathFor(SettingsScope.User));
        var preferences = new AppearanceSettings();
        restored.Register(preferences);
        Assert.True(preferences.NativeTitlebar);
        using var restoredApp = new GayaApplication(new ServiceCollection().BuildServiceProvider(),
            new PanelRegistry(), new CommandRegistry(), new MenuRegistry(), new ChromeRegistry(),
            new ShortcutService(NullLogger.Instance), new FocusTracker(), [],
            settings: restored);
        using var another = new Workbench(restoredApp, layoutStore: new WorkbenchLayoutStore(NullLogger.Instance,
            Path.Combine(Path.GetTempPath(), $"gaya-unused-layout-{Guid.NewGuid():N}.json")));
        Assert.True(another.NativeTitlebar);
        Assert.Same(preferences, another.Appearance);
        another.NativeTitlebar = true;
    }

    /// <summary>The Settings panel edits the window preference through its generated checkbox.</summary>
    [Fact]
    public void WindowPreferencesCanBeEditedInTheSettingsPanel()
    {
        using var frame = new Frame();
        using var shell = PluginHost.Load([], NullLogger.Instance);
        var panel = shell.Panels.All.Single(item => item.Id == ShellPanels.Settings).Factory(frame.Services);
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(frame.Gui, surface, font, panel.Render);
        var checkbox = Descendants(frame.Gui.RootNode!).Single(node =>
            node.Id == $"settings/{AppearanceSettings.PageId}/field5/editor");
        frame.Input.MousePosition.Returns(checkbox.Rect.Position + new Vector2(6, checkbox.Rect.H / 2));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        InspectorFormsRenderingTests.Frame(frame.Gui, surface, font, panel.Render);
        Assert.True(frame.Workbench.NativeTitlebar);
    }

    /// <summary>The compact menu still dispatches commands through their guards and dynamic labels.</summary>
    [Fact]
    public void CompactMenuDispatchesEnabledCommandsAndCollapsesAfterActivation()
    {
        using var frame = new Frame();
        var executions = 0;
        var enabled = false;
        frame.Commands.Register(new CommandDescriptor("test", "Original", _ => executions++, _ => enabled)
        { DynamicLabel = _ => "Current label" });
        frame.Menus.Add(new MenuItemDescriptor(MenuIds.File, "test"));
        frame.Draw();
        var menu = Descendants(frame.Gui.RootNode!).Single(node => node.Id == "menubar");
        frame.Input.MousePosition.Returns(menu.Rect.Position + new Vector2(15, 15));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        var row = Descendants(frame.Gui.RootNode!).Single(node => node.Id == "/menubar/File/v0/i0");
        frame.Input.MousePosition.Returns(row.Rect.Position + new Vector2(20, 10));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        Assert.Equal(0, executions);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        enabled = true;
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        Assert.Equal(1, executions);
        Assert.Equal("Current label", frame.Workbench.CommandLabel("test"));
        Assert.DoesNotContain(Descendants(frame.Gui.RootNode!), node => node.Id == "/menubar/File/v0");
    }

    /// <summary>Application window controls route close requests through the host's close capability.</summary>
    [Fact]
    public void WindowCloseButtonUsesTheGuardedCloseRequest()
    {
        using var frame = new Frame();
        var window = Substitute.For<IWindowChromeCapability>();
        window.CanMove.Returns(true);
        frame.Gui.Platform.Register(window);
        frame.Draw();
        var close = frame.Gui.RootNode!.Children[0].Children[0].Children[^1];
        frame.Input.MousePosition.Returns(close.Rect.Position + new Vector2(15, 15));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        window.Received(1).RequestClose();
    }

    /// <summary>A play-button press dispatches once during rendering and never while building the frame.</summary>
    [Fact]
    public void PlayButtonsDoNotDispatchDuringTheBuildPass()
    {
        using var frame = new Frame();
        var commands = Substitute.For<ICommandDispatcher>();
        commands.CanExecute(Arg.Any<string>()).Returns(true);
        var playMode = new PlayModeService(Substitute.For<IPlaySceneHost>(), null!,
            Substitute.For<IServiceProvider>(), NullLogger.Instance);
        frame.Chrome.Register(new ChromeDescriptor("play", ChromeSlot.MenuBar,
            _ => new PlayToolbarChrome(commands, playMode)));
        frame.Draw();
        var play = Descendants(frame.Gui.RootNode!).Single(node => node.Id == "play/play");
        frame.Input.MousePosition.Returns(play.Rect.Position + new Vector2(12, 12));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw(() => commands.DidNotReceive().Execute(Arg.Any<string>()));
        commands.Received(1).Execute("gaya.turian.play");
    }

    internal static IEnumerable<LayoutNode> Descendants(LayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
