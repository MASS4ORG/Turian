namespace Turian.Tests;

/// <summary>Checks .pss color themes: built-in parity, catalog sources, reloads, errors and token contributions.</summary>
[Collection(SerialTests.Name)]
public sealed class ThemeServiceTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), $"gaya-themes-{Guid.NewGuid():N}");

    /// <summary>Creates an empty user themes folder.</summary>
    public ThemeServiceTests() => Directory.CreateDirectory(UserFolder);

    string UserFolder => Path.Combine(directory, "themes");

    string UserSheet => Path.Combine(directory, "theme.user.pss");

    /// <inheritdoc />
    public void Dispose()
    {
        ThemeTokens.Current = ThemeTokens.Default;
        Directory.Delete(directory, recursive: true);
    }

#pragma warning disable CS0618
    /// <summary>The legacy palettes, which every built-in sheet reproduces exactly.</summary>
    public static TheoryData<string> BuiltIns() => [.. StudioTheme.BuiltIn.Select(theme => theme.Name)];

    /// <summary>Each built-in .pss compiles to the colors and metrics of the C# theme it replaced.</summary>
    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BuiltInSheetsReproduceTheLegacyThemes(string name)
    {
        var legacy = StudioTheme.BuiltIn.Single(theme => theme.Name == name);
        var service = Service();
        service.ApplyColorTheme(name);
        var tokens = service.Current;

        Assert.Equal("gaya." + name.ToLowerInvariant().Replace(' ', '-'), tokens.Id);
        Assert.Equal(legacy.IsDark, tokens.IsDark);
        Assert.Equal(
            [legacy.Background, legacy.Panel, legacy.Chrome, legacy.Field, legacy.Hover, legacy.Border, legacy.Ink,
                legacy.InkDim, legacy.InkFaint, legacy.Accent, legacy.AccentFill, legacy.EditorArea, legacy.Error,
                legacy.Warning, legacy.Folder],
            [
                tokens.Background, tokens.Panel, tokens.Chrome, tokens.Field, tokens.Hover, tokens.Border, tokens.Ink,
                tokens.InkDim, tokens.InkFaint, tokens.Accent, tokens.AccentFill, tokens.EditorArea, tokens.Error,
                tokens.Warning, tokens.Folder,
            ]);
        Assert.Equal([legacy.FontSize, legacy.MenuHeight, legacy.StatusHeight, legacy.HeaderHeight, legacy.RowHeight,
            legacy.Gap], [ tokens.FontSize, tokens.MenuHeight, tokens.StatusHeight, tokens.HeaderHeight,
            tokens.RowHeight, tokens.Gap ]);
        Assert.Equal(GuiColor.FromArgb(128, legacy.Accent), tokens.FocusRing);
        Assert.Equal(GuiColor.FromArgb(110, legacy.Accent), tokens.Selection);
        Assert.Equal(legacy.Panel, tokens.Popup);
        Assert.Null(service.Diagnostic);
    }

    /// <summary>The legacy registration converts a C# theme into a sheet with the same colors.</summary>
    [Fact]
    public void LegacyRegistrationConvertsToASheet()
    {
        var service = Service();
        service.Register(StudioTheme.Nord with { Name = "Legacy", FontSize = 14 });
        service.ApplyColorTheme("Legacy");
        Assert.Equal("Legacy", service.Current.Id);
        Assert.Equal(StudioTheme.Nord.Accent, service.Current.Accent);
        Assert.Equal(14f, service.Current.FontSize);
        Assert.Throws<ArgumentNullException>(() => service.Register((StudioTheme)null!));
    }
#pragma warning restore CS0618

    /// <summary>Built-ins are listed in menu order, and every built-in defines every catalog token.</summary>
    [Fact]
    public void BuiltInsListInMenuOrderWithCompleteTokens()
    {
        var service = Service();
        Assert.Equal(14, service.ColorThemes.Count);
        Assert.Equal(["Dark", "Light", "Dark Contrast"], service.ColorThemes.Take(3).Select(theme => theme.Name));
        Assert.Equal(ThemeKind.HighContrastDark, service.Catalog.Find("gaya.dark-contrast")!.Kind);
        Assert.DoesNotContain(service.ColorThemes, theme => theme.Id == ThemeCatalog.BaseId);

        foreach (var theme in service.ColorThemes)
        {
            var problems = new List<string>();
            var sheets = new StyleSheetCollection { service.Catalog.Load(theme.Id) };
            var tokens = ThemeCompiler.Compile(sheets, theme, problems);
            Assert.Empty(problems);
            Assert.Equal(16, tokens.Ansi.Count);
            Assert.Equal(Enum.GetValues<StatusKind>().Length, tokens.Statuses.Count);
        }
    }

    /// <summary>A three-seed theme derives every other token from gaya.base.</summary>
    [Fact]
    public void SeedOnlyThemeDerivesEveryToken()
    {
        var service = Service();
        service.Register(new ThemeSource("""
            @const theme-id = "test.seed";
            @const theme-name = "Seed";
            @const theme-kind = light;
            @import "gaya.base";
            $base = #e0e0e0;
            $accent = #3366cc;
            $contrast = -0.06;
            """));
        service.ApplyColorTheme("test.seed");
        var tokens = service.Current;

        Assert.Equal(ThemeKind.Light, tokens.Kind);
        Assert.False(tokens.IsDark);
        Assert.Equal(GuiColor.FromArgb(255, 0xe0, 0xe0, 0xe0), tokens.Panel);
        Assert.True(tokens.Ink.R < 64, "ink follows contrast-ink of a light base");
        Assert.Equal(GuiColor.FromArgb(255, 0x33, 0x66, 0xcc), tokens.Accent);
        Assert.Equal(tokens.GetColor("surface-panel"), tokens.Panel);
        Assert.Equal(tokens.Status(StatusKind.Created).Color, tokens.GetColor("$created"));
        Assert.Equal(6f, tokens.GetLength("gap"));
        Assert.Equal("0.12", tokens.GetValue("transition-fast"));
        Assert.Null(tokens.GetValue("missing"));
        Assert.Equal(GuiColor.Red, tokens.GetColor("missing", GuiColor.Red));
        Assert.Equal(9f, tokens.GetLength("missing", 9f));
    }

    /// <summary>A sheet dropped into the user folder is offered, wins over a built-in id, and reloads on save.</summary>
    [Fact]
    public void UserThemesAppearOverrideAndHotReload()
    {
        var service = Service();
        var path = Path.Combine(UserFolder, "mine", "ocean.pss");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Sheet("test.ocean", "Ocean", "#102030"));
        File.WriteAllText(Path.Combine(UserFolder, "dark.pss"), Sheet("gaya.dark", "My Dark", "#010203"));
        Assert.True(service.ReloadIfChanged());
        Assert.False(service.ReloadIfChanged());

        var ocean = Assert.Single(service.ColorThemes, theme => theme.Id == "test.ocean");
        Assert.Equal(ThemeOrigin.User, ocean.Origin);
        Assert.Equal(path, ocean.Path);
        Assert.Equal("My Dark", service.ColorThemes[0].Name);
        Assert.Equal(GuiColor.FromArgb(255, 1, 2, 3), service.Current.Accent);

        var changes = 0;
        service.Changed += () => changes++;
        service.ApplyColorTheme("Ocean");
        Assert.Equal("test.ocean", service.CommittedColorTheme);
        File.WriteAllText(path, Sheet("test.ocean", "Ocean", "#405060"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.True(service.ReloadIfChanged());
        Assert.Equal(GuiColor.FromArgb(255, 0x40, 0x50, 0x60), service.Current.Accent);
        Assert.True(changes >= 2);
    }

    /// <summary>A broken sheet keeps the last valid theme on screen and reports where the error is.</summary>
    [Fact]
    public void BrokenSheetKeepsLastValidThemeWithLocatedError()
    {
        var logger = new ListLogger();
        var service = Service(logger);
        var path = Path.Combine(UserFolder, "live.pss");
        File.WriteAllText(path, Sheet("test.live", "Live", "#112233"));
        service.ReloadIfChanged();
        service.ApplyColorTheme("test.live");
        var valid = service.Current;
        var sheets = service.Sheets;

        File.WriteAllText(path, Sheet("test.live", "Live", "#112233") + "\nbutton { radius = mix(; }\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        service.ReloadIfChanged();

        Assert.Equal(valid.Accent, service.Current.Accent);
        Assert.Same(sheets, service.Sheets);
        var diagnostic = Assert.IsType<ThemeDiagnostic>(service.Diagnostic);
        Assert.Equal(Path.GetFullPath(path), diagnostic.Source);
        Assert.Equal(7, diagnostic.Line);
        Assert.Contains(path, diagnostic.Message);
        Assert.Single(logger.Errors);

        File.WriteAllText(path, Sheet("test.live", "Live", "#445566"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
        service.ReloadIfChanged();
        Assert.Null(service.Diagnostic);
        Assert.Equal(GuiColor.FromArgb(255, 0x44, 0x55, 0x66), service.Current.Accent);
    }

    /// <summary>A broken committed theme at startup falls back to the default instead of drawing nothing.</summary>
    [Fact]
    public void BrokenThemeAtStartupFallsBackToDefault()
    {
        File.WriteAllText(Path.Combine(UserFolder, "bad.pss"), """
            @const theme-id = "test.bad";
            @import "missing.sheet";
            """);
        var service = Service();
        service.ApplyColorTheme("test.bad");
        Assert.Equal("test.bad", service.CommittedColorTheme);
        Assert.Equal("gaya.dark", service.Current.Id);
        Assert.Contains("missing.sheet", service.Diagnostic!.Message);
    }

    /// <summary>A persisted theme registered after startup, such as a brick theme, is applied once it appears.</summary>
    [Fact]
    public void PersistedThemeAppliesWhenItAppearsLater()
    {
        var service = Service();
        service.ApplyColorTheme("brick.sunset");
        Assert.Equal("brick.sunset", service.CommittedColorTheme);
        Assert.Equal("gaya.dark", service.Current.Id);

        var brick = Path.Combine(directory, "brick", "Themes");
        Directory.CreateDirectory(brick);
        File.WriteAllText(Path.Combine(brick, "sunset.pss"), Sheet("brick.sunset", "Sunset", "#ff8800"));
        service.SetBrickFolders([brick]);

        Assert.Equal("brick.sunset", service.Current.Id);
        Assert.Equal(ThemeOrigin.Brick, service.Catalog.Find("Sunset")!.Origin);
        Assert.Equal([brick], service.Catalog.BrickFolders);
    }

    /// <summary>The user override sheet and settings tokens layer above the theme.</summary>
    [Fact]
    public void UserLayersOverrideTheTheme()
    {
        File.WriteAllText(UserSheet, "$accent = #00ff00;\n$gap = 9;");
        var service = Service();
        Assert.Equal(GuiColor.FromArgb(255, 0, 255, 0), service.Current.Accent);
        Assert.Equal(9f, service.Current.Gap);

        service.SetUserToken("gap", "3");
        Assert.Equal(9f, service.Current.Gap);
        service.SetUserToken("row-height", "30");
        Assert.Equal(30f, service.Current.RowHeight);
        service.SetUserToken("row-height", null);
        Assert.Equal(20f, service.Current.RowHeight);

        File.Delete(UserSheet);
        Assert.True(service.ReloadIfChanged());
        Assert.Equal(3f, service.Current.Gap);
    }

    /// <summary>Plugin tokens get their default, follow other tokens, and are overridable by a theme.</summary>
    [Fact]
    public void PluginTokenContributionsHaveDefaultsThemesOverride()
    {
        var service = Service();
        service.Token("test-glow", "alpha($accent, 0.5)");
        Assert.Equal(GuiColor.FromArgb(128, 140, 140, 140), service.Current.GetColor("test-glow"));

        service.Register(new ThemeSource("""
            @const theme-id = "test.glow";
            @import "gaya.base";
            $test-glow = #123456;
            """));
        service.ApplyColorTheme("test.glow");
        Assert.Equal(GuiColor.FromArgb(255, 0x12, 0x34, 0x56), service.Current.GetColor("test-glow"));
        Assert.Equal("test.glow", service.Current.Name);

        Assert.Throws<ArgumentException>(() => service.Token("bad.name", "#fff"));
        Assert.Throws<StyleSheetException>(() => service.Token("bad-value", "mix(;"));
        Assert.Throws<ArgumentException>(() => service.Register(new ThemeSource("$x = 1;")));
    }

    /// <summary>Previews last one frame, and the workbench installs the theme's sheets into the GUI.</summary>
    [Fact]
    public void PreviewIsDroppedAndSheetsReachTheGui()
    {
        using var frame = new GayaChromeTests.Frame();
        frame.Draw();
        Assert.Equal(frame.Themes.Sheets.Count, frame.Gui.StyleSheets.Count);
        Assert.Equal(frame.Themes.Current.Panel, frame.Gui.StyleSheets.GetTokenColor("surface-panel"));

        frame.Themes.PreviewColorTheme("gaya.nord");
        Assert.Equal("gaya.nord", frame.Themes.Current.Id);
        frame.Draw();
        Assert.Equal("#3b4252", frame.Gui.StyleSheets.GetToken("surface-panel"));
        frame.Themes.EndFrame();
        frame.Themes.EndFrame();
        Assert.Equal("gaya.dark", frame.Themes.Current.Id);
        frame.Themes.PreviewColorTheme("unknown");
        Assert.Equal("gaya.dark", frame.Themes.Current.Id);
        Assert.Equal(frame.Themes.Current, ThemeTokens.Current);
    }

    /// <summary>The theme sheets give Guinevere's controls their tokens, and the dock space its metrics.</summary>
    [Fact]
    public void ThemeSheetsDefineTheControlTokens()
    {
        var service = Service();
        var tokens = service.Current;
        var sheets = new StyleSheetCollection { ExcaliburStyles.DefaultSheet };
        foreach (var sheet in service.Sheets) sheets.Add(sheet);

        Assert.Equal(tokens.Popup, sheets.GetTokenColor("popup"));
        Assert.Equal(tokens.Field, sheets.GetTokenColor("surface"));
        Assert.Equal(tokens.Ink, sheets.GetTokenColor("text"));
        Assert.Equal(tokens.FocusRing, sheets.GetTokenColor("focus-ring"));
        Assert.Equal(tokens.Selection, sheets.GetTokenColor("text-selection"));
        Assert.Equal(tokens.Scale(tokens.HeaderHeight), tokens.ToDockTheme().TabHeight);
    }

    ThemeService Service(ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        return new ThemeService(log, new ThemeCatalog(log, UserFolder), UserSheet);
    }

    static string Sheet(string id, string name, string accent) => $"""
        // test theme
        @const theme-id = "{id}";
        @const theme-name = "{name}";
        @import "gaya.base";

        $accent = {accent};
        """;

    sealed class ListLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
