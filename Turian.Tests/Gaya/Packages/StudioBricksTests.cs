namespace Turian.Tests;

/// <summary>Studio-scope bricks managed through the Bricks panel with no project open.</summary>
[Collection(SerialTests.Name)]
public sealed class StudioBricksTests : IDisposable
{
    static readonly Dictionary<string, SemanticVersion> Hosts = new() { ["gaya"] = SemanticVersion.Parse("1.0.0") };

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-studio-bricks-{Guid.NewGuid():N}");
    readonly StudioBricks studio;

    /// <summary>Creates an empty studio folder and a local theme brick beside it.</summary>
    public StudioBricksTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "studio"));
        studio = new StudioBricks(Path.Combine(root, "studio"), Hosts, new PackageStore(Path.Combine(root, "store")));
        ThemeBrick("sunset", "com.acme.sunset", "acme.sunset", "Sunset");
    }

    string Source(string folder) => $"file:{Path.Combine(root, folder)}";

    /// <inheritdoc/>
    public void Dispose()
    {
        ThemeTokens.Current = ThemeTokens.Default;
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A studio declares, disables and removes bricks, and rolls back a declaration that does not resolve.</summary>
    [Fact]
    public void StudioDeclaresDisablesAndRollsBack()
    {
        Assert.False(studio.HasManifest);
        Assert.Empty(studio.Resolve());
        Assert.Equal(["gaya"], studio.ReservedCategoryPrefixes);

        Assert.Equal("com.acme.sunset", studio.AddFromSource(Source("sunset"), new PackageStore(root)));
        var brick = Assert.Single(studio.Resolve());
        Assert.False(studio.IsPrecast(brick));
        Assert.True(File.Exists(Path.Combine(studio.Root, "Bricks", LockFile.FileName)));

        Assert.Throws<PackageException>(() => studio.Add("com.acme.missing", Source("missing")));
        Assert.Single(studio.Resolve());

        studio.Disable("com.acme.sunset");
        Assert.Empty(studio.Resolve());
        Assert.Throws<PackageException>(() => studio.Disable("com.acme.sunset"));
        Assert.False(studio.Remove("com.acme.sunset"));

        Assert.Throws<PackageException>(() => studio.Embed("x"));
        Assert.Throws<PackageException>(() => studio.Revert("x"));
        Assert.Throws<PackageException>(() => studio.CopyAssets("x", [], "y"));
        Assert.False(studio.SupportsForks);
        Assert.Null(studio.BuiltinDirectory);
    }

    /// <summary>A project-only brick is refused by the studio and the manifest is put back.</summary>
    [Fact]
    public void ProjectOnlyBricksAreRefused()
    {
        var path = Path.Combine(root, "tool");
        Directory.CreateDirectory(path);
        new PackageManifest { Name = "com.acme.tool", Version = SemanticVersion.Parse("1.0.0") }.Save(path);

        Assert.Throws<PackageException>(() => studio.Add("com.acme.tool", Source("tool")));
        Assert.Empty(studio.Resolve());
        Assert.False(File.Exists(Path.Combine(studio.Root, "Bricks", ProjectManifest.FileName)));
    }

    /// <summary>Registries are declared in the studio manifest, validated, and listed before the public one.</summary>
    [Fact]
    public void StudioRegistriesAreValidated()
    {
        var shared = new ScopedRegistry { Name = "public", Url = "https://example.invalid", Scopes = ["org"], AllowUnsigned = true };
        var withPublic = new StudioBricks(studio.Root, Hosts, publicRegistry: shared);
        Assert.Equal([shared], withPublic.Registries());

        withPublic.AddRegistry(new ScopedRegistry { Name = "acme", Url = "https://acme.invalid", Scopes = ["com.acme"], AllowUnsigned = true });
        Assert.Equal(["acme", "public"], withPublic.Registries().Select(r => r.Name));
        Assert.Throws<PackageException>(() => withPublic.AddRegistry(new ScopedRegistry { Name = "bad" }));
        Assert.Throws<PackageException>(() => withPublic.AddRegistry(new ScopedRegistry
        {
            Name = "unsigned",
            Url = "https://u.invalid",
            Scopes = ["u"],
        }));
        Assert.True(withPublic.RemoveRegistry("acme"));
        Assert.False(withPublic.RemoveRegistry("acme"));

        var manifest = ProjectManifest.Load(studio.Root).Manifest;
        manifest.ScopedRegistries.Add(new ScopedRegistry { Name = "x" });
        Assert.Throws<PackageException>(() => withPublic.SaveManifest(manifest));
    }

    /// <summary>With no project open, the panel manages the studio's bricks and filters them by category.</summary>
    [Fact]
    public async Task PanelManagesStudioBricksWithoutAProject()
    {
        var controller = new BricksController(studio, null);
        var panel = new BricksPanel(controller);
        using var frame = new PanelFrame(panel);
        frame.Draw();

        Assert.Equal(BrickScope.Studio, controller.Scope);
        Assert.False(controller.HasProject);
        Assert.True(controller.HasWorkspace);
        Assert.True(await controller.AddFromSourceAsync(Source("sunset")));
        frame.Draw();

        Assert.Equal(["com.acme.sunset"], controller.Rows.Select(row => row.Id));
        Assert.Contains(frame.Nodes(), node => node.Id == "bricks/row/com.acme.sunset");
        Assert.Contains(frame.Nodes(), node => node.Id == "bricks/category/Themes");

        panel.Category = BrickCategoryFilter.Fonts;
        frame.Draw();
        Assert.DoesNotContain(frame.Nodes(), node => node.Id == "bricks/row/com.acme.sunset");
        panel.Browse(BrickCategoryFilter.Themes);
        frame.Draw();
        Assert.Contains(frame.Nodes(), node => node.Id == "bricks/row/com.acme.sunset");

        controller.Scope = BrickScope.Project;
        frame.Draw();
        Assert.False(controller.HasWorkspace);
        Assert.False(await controller.InstallAsync("x", null));
        Assert.Equal("Open a project first.", controller.Error);
        Assert.Contains(frame.Nodes(), node => node.Id == "bricks/scope/Studio");
    }

    /// <summary>Installing a content-only theme brick makes its theme selectable without a restart.</summary>
    [Fact]
    public async Task ThemeBrickAppliesWithoutRestart()
    {
        var themes = new ThemeService(NullLogger.Instance,
            new ThemeCatalog(NullLogger.Instance, Path.Combine(root, "themes")), Path.Combine(root, "theme.user.pss"));
        StudioThemeBricks.Bind(studio, themes, NullLogger.Instance);
        themes.ApplyColorTheme("acme.sunset");
        Assert.Equal("gaya.dark", themes.Current.Id);

        var controller = new BricksController(studio, null) { Scope = BrickScope.Studio };
        Assert.True(await controller.AddFromSourceAsync(Source("sunset")));
        themes.EndFrame();

        Assert.Equal("acme.sunset", themes.Current.Id);
        Assert.Equal(ThemeOrigin.Brick, themes.Catalog.Find("Sunset")!.Origin);
        Assert.Equal([Path.Combine(root, "sunset", "Themes")],
            PackagedPlugins.ThemeFolders(studio.Resolve()));
    }

    void ThemeBrick(string folder, string id, string themeId, string name)
    {
        var path = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(path, "Themes"));
        new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("1.0.0"),
            Scopes = [PackageScope.Studio],
            Categories = [ThemeCategories.ColorTheme],
        }.Save(path);
        File.WriteAllText(Path.Combine(path, "Themes", $"{folder}.pss"), $"""
            @const theme-id = "{themeId}";
            @const theme-name = "{name}";
            @import "gaya.base";
            $accent = #ff8800;
            """);
    }

    /// <summary>Renders one panel headlessly, both passes per frame.</summary>
    sealed class PanelFrame(IPanel panel) : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(640, 480));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly Gui gui = new() { Input = Substitute.For<IInputHandler>() };

        public void Draw()
        {
            gui.SetStage(Pass.Pass1Build);
            gui.BeginFrame(surface.Canvas, font, font);
            panel.Render(gui);
            gui.CalculateLayout();
            gui.SetStage(Pass.Pass2Render);
            panel.Render(gui);
            gui.Render();
            gui.EndFrame();
        }

        public IEnumerable<LayoutNode> Nodes() => GayaChromeTests.Descendants(gui.RootNode!);

        public void Dispose() => surface.Dispose();
    }
}
