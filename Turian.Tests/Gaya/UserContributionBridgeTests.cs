namespace Turian.Tests;

/// <summary>Checks that discovered user objects reach Gaya's settings and panel APIs across republication.</summary>
[Collection(SerialTests.Name)]
public sealed class UserContributionBridgeTests
{
    /// <summary>A user panel that owns its custom body and header rendering.</summary>
    [Panel("Custom header", "Tools/Custom header")]
    public sealed class CustomPanel : IPanel
    {
        /// <inheritdoc />
        public void Render(Gui gui) { }

        /// <inheritdoc />
        public void RenderHeader(Gui gui, PanelHeaderContext context) => gui.DrawText("Custom header");
    }

    /// <summary>User settings retain attribute metadata and replace the live object when code is republished.</summary>
    [Fact]
    public void UserSettingsUseAttributeConfigurationAcrossRepublication()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var catalog = new UserSettingsCatalog(build, NullLogger.Instance);
        var pages = (IList<UserSettingsPage>)catalog.Pages;
        var discovered = UserSettingsCatalog.Scan(typeof(SettingsMetadataTests.CustomPage).Assembly,
            NullLogger.Instance).Single(page => page.Target is SettingsMetadataTests.CustomPage);
        var directory = Directory.CreateTempSubdirectory("gaya-user-settings-").FullName;
        try
        {
            var settings = new EditorSettings(NullLogger.Instance, Path.Combine(directory, "user.json"));
            settings.BindWorkspace(directory);
            using var bridge = new UserSettingsBridge(catalog, settings, NullLogger.Instance);
            pages.Add(discovered);
            NotifyChanged(catalog);
            var registered = Assert.Single(settings.Pages);
            Assert.Same(discovered.Target, registered.Target);
            Assert.True(registered.Hidden);
            Assert.Equal(SettingsScope.Workspace, registered.Scope);
            Assert.Equal("custom.tools", registered.Id);
            Assert.Equal("Custom/Tools", registered.Path);
            var replacement = new SettingsMetadataTests.CustomPage();
            pages[0] = discovered with { Target = replacement };
            NotifyChanged(catalog);
            Assert.Same(replacement, Assert.Single(settings.Pages).Target);
            pages.Clear();
            NotifyChanged(catalog);
            Assert.Empty(settings.Pages);
            bridge.Sync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A custom panel instance is used directly, while plain user objects receive reflected forms.</summary>
    [Fact]
    public void UserPanelsOwnTheirRenderingAndReplaceTheirContributions()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var catalog = new UserPanelCatalog(build, NullLogger.Instance);
        var pages = (IList<UserPanelPage>)catalog.Pages;
        var discovered = UserPanelCatalog.Scan(typeof(CustomPanel).Assembly, NullLogger.Instance);
        var custom = discovered.Single(page => page.Target is CustomPanel);
        var plain = discovered.Single(page => page.Target is UserPanelCatalogTests.Fixture);
        var panels = new PanelRegistry();
        var commands = new CommandRegistry();
        var menus = new MenuRegistry();
        using var bridge = new UserPanelBridge(catalog, panels, commands, menus, NullLogger.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        pages.Add(custom);
        pages.Add(plain);
        NotifyChanged(catalog);
        Assert.Same(custom.Target, panels.All.Single(panel => panel.Id == custom.Id).Factory(services));
        Assert.IsType<UserPanel>(panels.All.Single(panel => panel.Id == plain.Id).Factory(services));
        Assert.All(panels.All, panel => Assert.False(panel.OpenByDefault));
        Assert.NotNull(commands.Find(custom.Id));
        var replacement = new CustomPanel();
        pages[0] = custom with { Target = replacement };
        NotifyChanged(catalog);
        Assert.Equal(2, panels.All.Count);
        Assert.Same(replacement, panels.All.Single(panel => panel.Id == custom.Id).Factory(services));
        pages.Clear();
        NotifyChanged(catalog);
        Assert.Empty(panels.All);
        Assert.Null(commands.Find(custom.Id));
        bridge.Sync();
    }

    static void NotifyChanged(object catalog) =>
        ((Action)catalog.GetType().GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(catalog)!)();
}
