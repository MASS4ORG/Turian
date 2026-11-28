namespace Turian.Tests;

/// <summary>Checks that discovered user objects reach Gaya's settings and panel APIs across republication.</summary>
[Collection(SerialTests.Name)]
public sealed class UserContributionBridgeTests
{
    /// <summary>A user panel that owns its custom body and header rendering.</summary>
    [Panel("Custom header", "Tools/Custom header")]
    public sealed class CustomPanel : IPanel, IDisposable
    {
        /// <summary>Counts calls to the catalog-owned target's disposal.</summary>
        public int DisposeCount { get; private set; }

        /// <summary>Counts body rendering calls.</summary>
        public int BodyCalls { get; private set; }

        /// <summary>Counts header rendering calls.</summary>
        public int HeaderCalls { get; private set; }

        /// <summary>The last header context delivered to the target.</summary>
        public PanelHeaderContext LastHeaderContext { get; private set; }

        /// <inheritdoc />
        public void Render(Gui gui) => BodyCalls++;

        /// <inheritdoc />
        public void RenderHeader(Gui gui, PanelHeaderContext context)
        {
            HeaderCalls++;
            LastHeaderContext = context;
        }

        /// <inheritdoc />
        public void Dispose() => DisposeCount++;
    }

    /// <summary>User settings retain attribute metadata and replace the live object when code is republished.</summary>
    [Fact]
    public void UserSettingsUseAttributeConfigurationAcrossRepublication()
    {
        using var build = new BuildManager(new AppSettings(), NullLogger.Instance);
        var catalog = new UserSettingsCatalog(build, NullLogger.Instance);
        var pages = (IList<UserSettingsPage>)catalog.Pages;
        var discovered = UserSettingsCatalog.Scan(typeof(SettingsMetadataTests.CustomPage).Assembly,
            NullLogger.Instance).Single(static page => page.Target is SettingsMetadataTests.CustomPage);
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
        var customView = panels.All.Single(panel => panel.Id == custom.Id).Factory(services);
        Assert.IsAssignableFrom<IPanel>(customView);
        Assert.NotSame(custom.Target, customView);
        Assert.IsNotAssignableFrom<IDisposable>(customView);
        customView.Render(null!);
        var headerContext = new PanelHeaderContext(custom.Id, custom.Name, default, true, services);
        customView.RenderHeader(null!, headerContext);
        Assert.Equal(headerContext, ((CustomPanel)custom.Target).LastHeaderContext);
        Assert.Equal(1, ((CustomPanel)custom.Target).BodyCalls);
        Assert.Equal(1, ((CustomPanel)custom.Target).HeaderCalls);
        Assert.Equal(0, ((CustomPanel)custom.Target).DisposeCount);
        Assert.IsType<UserPanel>(panels.All.Single(panel => panel.Id == plain.Id).Factory(services));
        Assert.All(panels.All, panel => Assert.False(panel.OpenByDefault));
        Assert.NotNull(commands.Find(custom.Id));
        Assert.Contains(menus.ItemsFor("menubar/Tools"), item => item.CommandId == custom.Id);
        var replacement = new CustomPanel();
        pages[0] = custom with { Target = replacement };
        NotifyChanged(catalog);
        Assert.Equal(2, panels.All.Count);
        var replacementView = panels.All.Single(panel => panel.Id == custom.Id).Factory(services);
        Assert.NotSame(replacement, replacementView);
        Assert.IsNotAssignableFrom<IDisposable>(replacementView);
        pages.Clear();
        NotifyChanged(catalog);
        Assert.Empty(panels.All);
        Assert.Null(commands.Find(custom.Id));
        Assert.DoesNotContain(menus.ItemsFor("menubar/Tools"), item => item.CommandId == custom.Id);
        Assert.Equal(0, ((CustomPanel)custom.Target).DisposeCount);

        pages.Add(custom);
        NotifyChanged(catalog);
        bridge.Dispose();
        Assert.Empty(panels.All);
        Assert.Null(commands.Find(custom.Id));
        Assert.DoesNotContain(menus.ItemsFor("menubar/Tools"), item => item.CommandId == custom.Id);
        Assert.Equal(0, ((CustomPanel)custom.Target).DisposeCount);
        bridge.Sync();
    }

    static void NotifyChanged(object catalog) =>
        ((Action)catalog.GetType().GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(catalog)!)();
}
