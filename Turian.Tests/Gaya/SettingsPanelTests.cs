namespace Turian.Tests;

/// <summary>
/// The Settings panel is the host's own: a Gaya application with no plugin at all offers it, and it draws and edits
/// settings pages with the shared form renderer.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class SettingsPanelTests
{
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    sealed class SamplePage
    {
        public int Volume { get; set; } = 3;
        public bool Muted { get; set; }
    }

    /// <summary>With no plugin loaded, the host contributes the panel, its File entry and its shortcut.</summary>
    [Fact]
    public void HostContributesSettingsWithoutPlugins()
    {
        using var app = PluginHost.Load([], NullLogger.Instance);

        Assert.Empty(app.LoadedPluginIds);
        Assert.Contains(app.Panels.All, panel => panel.Id == ShellPanels.Settings);
        Assert.NotNull(app.Commands.Find(ShellCommands.Settings));
        Assert.Contains(app.Menus.ItemsFor(MenuIds.File), item => item.CommandId == ShellCommands.Settings);
        Assert.NotEmpty(app.Shortcuts.DisplayFor(ShellCommands.Settings));
    }

    /// <summary>
    /// Without any localization registered, a page draws untranslated and an edit through its form reaches the
    /// page object and marks the settings changed.
    /// </summary>
    [Fact]
    public void PageDrawsAndEditsWithoutLocalization()
    {
        using var app = PluginHost.Load([], NullLogger.Instance);
        var page = new SamplePage();
        app.Settings.Register(new SettingsPageDescriptor("sample", "Audio", page, Order: -1));
        var changes = 0;
        app.Settings.Changed += () => changes++;
        var panel = app.Panels.All.Single(p => p.Id == ShellPanels.Settings).Factory(app.Services);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1, -1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        var font = Font.FromFamilyName("sans-serif", 14);

        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        var muted = Find(gui.RootNode!, "settings/sample/field1/editor");
        Assert.NotNull(Find(gui.RootNode!, "settings/sample/field0/editor"));
        Assert.NotNull(muted);

        input.MousePosition.Returns(new Vector2(muted.Rect.X + 6, muted.Rect.Y + muted.Rect.H / 2));
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);

        Assert.True(page.Muted);
        Assert.True(changes > 0);
    }

    /// <summary>Root options remain reachable beside nested sections, with only categories in the left list.</summary>
    [Fact]
    public void TopLevelCategoryShowsRootAndNestedPages()
    {
        using var app = PluginHost.Load([], NullLogger.Instance);
        app.Settings.Register(new SettingsPageDescriptor("root", "Scene Viewer", new SamplePage(), Order: -10));
        app.Settings.Register(new SettingsPageDescriptor("gizmos", "Scene Viewer/Gizmos", new SamplePage()));
        app.Settings.Register(new SettingsPageDescriptor("nested", "Scene Viewer/Gizmos/Colors", new SamplePage()));
        app.Settings.Register(new SettingsPageDescriptor("grid", "Scene Viewer/Grid", new SamplePage()));
        var panel = app.Panels.All.Single(p => p.Id == ShellPanels.Settings).Factory(app.Services);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(1000, 1600));
        var font = Font.FromFamilyName("sans-serif", 14);
        void Frame() => InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        Frame();
        Frame();
        foreach (var id in new[] { "root", "gizmos", "nested", "grid" })
            Assert.NotNull(Find(gui.RootNode!, $"settings/{id}/field0/editor"));
        foreach (var path in new[] { "Scene Viewer", "Scene Viewer/Gizmos", "Scene Viewer/Gizmos/Colors", "Scene Viewer/Grid" })
            Assert.NotNull(Find(gui.RootNode!, "settings/heading/" + path));
        var rows = (IReadOnlyList<TreeItem>)panel.GetType().GetMethod("Rows", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, [app.Settings.Pages])!;
        Assert.Single(rows, row => row.Id == "Scene Viewer");
        Assert.All(rows, row => { Assert.Equal(0, row.Depth); Assert.False(row.HasChildren); });
        var select = panel.GetType().GetMethod("OnCategoryClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        select.Invoke(panel, [new TreeViewEvent(rows.Single(row => row.Id == "Appearance"), GMouseButton.Left, 1)]);
        Frame();
        Assert.Null(Find(gui.RootNode!, "settings/root/field0/editor"));
        Assert.NotNull(Find(gui.RootNode!, "settings/heading/Appearance"));
        select.Invoke(panel, [new TreeViewEvent(rows.Single(row => row.Id == "Scene Viewer"), GMouseButton.Left, 1)]);
        Frame();
        Assert.NotNull(Find(gui.RootNode!, "settings/root/field0/editor"));
    }

    /// <summary>An empty registry renders without choosing a category or inventing a section.</summary>
    [Fact]
    public void EmptyRegistryHasNoCategoryHeading()
    {
        using var app = PluginHost.Load([], NullLogger.Instance);
        foreach (var page in app.Settings.Pages.ToArray()) app.Settings.Remove(page.Id);
        var panel = app.Panels.All.Single(p => p.Id == ShellPanels.Settings).Factory(app.Services);
        var input = Substitute.For<IInputHandler>();
        input.MousePosition.Returns(new Vector2(-1));
        var gui = new Gui { Input = input };
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        var font = Font.FromFamilyName("sans-serif", 14);
        InspectorFormsRenderingTests.Frame(gui, surface, font, panel.Render);
        Assert.Null(Find(gui.RootNode!, "settings/heading/Scene Viewer"));
    }

    static LayoutNode? Find(LayoutNode node, string id)
    {
        if (node.Id == id) return node;
        foreach (var child in node.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }
}
