namespace Turian.Tests;

/// <summary>Checks looks and icon themes: catalog sources, base-sheet validation, pairing, previews and persistence.</summary>
[Collection(SerialTests.Name)]
public sealed class ThemeLookTests : IDisposable
{
    const string Square = """
        @const look-id = "test.square";
        @const look-name = "Square";
        @const look-theme-dark = "test.night";
        @const look-theme-light = "test.day";
        @import "guinevere.default";
        button { border-radius = 0; }
        """;

    readonly string directory = Path.Combine(Path.GetTempPath(), $"gaya-looks-{Guid.NewGuid():N}");

    /// <summary>Creates an empty user themes folder.</summary>
    public ThemeLookTests() => Directory.CreateDirectory(UserFolder);

    string UserFolder => Path.Combine(directory, "themes");

    /// <inheritdoc />
    public void Dispose()
    {
        ThemeTokens.Current = ThemeTokens.Default;
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>The default look is listed first, leaves Guinevere's base sheet in place and has no extra rules.</summary>
    [Fact]
    public void DefaultLookLeavesTheBaseSheetAlone()
    {
        var service = Service();
        var material = Assert.Single(service.Looks);
        Assert.Equal(ThemeCatalog.DefaultLook, material.Id);
        Assert.Equal(ThemeCategories.Look, material.Category);
        Assert.Equal(ThemeCatalog.DefaultLook, service.CommittedLook);
        Assert.Null(service.Look);
        Assert.DoesNotContain(service.ColorThemes, theme => theme.Category == ThemeCategories.Look);
    }

    /// <summary>A look from any source becomes the base sheet and swaps back to the default.</summary>
    [Fact]
    public void AppliedLookBecomesTheBaseSheet()
    {
        var service = Service();
        service.Register(new ThemeSource(Square));
        var version = service.SheetsVersion;

        service.ApplyLook("test.square");

        Assert.NotNull(service.Look);
        var base_ = new StyleSheetCollection { service.Look };
        Assert.StartsWith("0", base_.Resolve(new StyleTarget("button", null, [])).Get("border-radius"));
        Assert.True(service.SheetsVersion > version);
        Assert.Null(service.Diagnostic);

        service.ApplyLook(ThemeCatalog.DefaultLook);
        Assert.Null(service.Look);
        Assert.Equal(ThemeCatalog.DefaultLook, service.CommittedLook);
    }

    /// <summary>A look that is not a complete base sheet is reported and skipped; the colors still apply.</summary>
    [Fact]
    public void IncompleteLookIsReportedAndSkipped()
    {
        var service = Service();
        service.Register(new ThemeSource("""
            @const look-id = "test.partial";
            button { border-radius = 0; }
            """));

        service.ApplyLook("test.partial");
        service.ApplyColorTheme("gaya.nord");

        Assert.Null(service.Look);
        Assert.Equal("gaya.nord", service.Current.Id);
        Assert.Contains("complete base sheet", service.Diagnostic!.Message);
        Assert.Equal("test.partial", service.CommittedLook);

        service.ApplyLook(ThemeCatalog.DefaultLook);
        Assert.Null(service.Diagnostic);
    }

    /// <summary>A look suggests its dark or light theme by the kind of the committed color theme.</summary>
    [Fact]
    public void LookSuggestsAThemeOfTheCurrentKind()
    {
        var service = Service();
        service.Register(new ThemeSource(Square));
        service.Register(new ThemeSource(ThemeSheet("test.night", "dark")));
        service.Register(new ThemeSource(ThemeSheet("test.day", "light")));

        Assert.Equal("test.night", service.DefaultColorThemeFor("test.square"));
        service.ApplyColorTheme("gaya.light");
        Assert.Equal("test.day", service.DefaultColorThemeFor("test.square"));
        Assert.Null(service.DefaultColorThemeFor(ThemeCatalog.DefaultLook));
        Assert.Null(service.DefaultColorThemeFor("gaya.dark"));
        Assert.Null(service.DefaultColorThemeFor("missing"));

        service.ApplyLook("test.square");
        Assert.Equal("gaya.light", service.CommittedColorTheme);
    }

    /// <summary>A suggested theme that is not installed is not offered.</summary>
    [Fact]
    public void MissingSuggestedThemeIsIgnored()
    {
        var service = Service();
        service.Register(new ThemeSource(Square));
        Assert.Null(service.DefaultColorThemeFor("test.square"));
    }

    /// <summary>Each selection previews on its own and drops when its menu stops renewing it.</summary>
    [Fact]
    public void PreviewsAreIndependentAndShortLived()
    {
        var service = Service();
        service.Register(new ThemeSource(Square));
        service.Register(new ThemeSource(Icons("test.icons")));

        service.PreviewLook("test.square");
        service.PreviewIconTheme("test.icons");
        Assert.NotNull(service.Look);
        Assert.Equal(ThemeCatalog.DefaultLook, service.CommittedLook);
        Assert.Equal(ThemeCatalog.DefaultIconTheme, service.CommittedIconTheme);

        service.EndFrame();
        Assert.NotNull(service.Look);
        Assert.NotNull(Glyph(service));

        service.PreviewIconTheme("test.icons");
        service.EndFrame();
        Assert.Null(service.Look);
        Assert.NotNull(Glyph(service));

        service.EndFrame();
        Assert.Null(Glyph(service));
        service.PreviewLook("unknown");
        Assert.Null(service.Look);
    }

    /// <summary>An icon theme joins the stack above the color theme and is swapped by id.</summary>
    [Fact]
    public void IconThemeJoinsTheStack()
    {
        var service = Service();
        service.Register(new ThemeSource(Icons("test.icons")));
        Assert.Contains(service.IconThemes, theme => theme.Id == "test.icons");

        service.ApplyIconTheme("test.icons");

        Assert.Equal("x", Glyph(service)?.Trim('"'));
        Assert.Equal("test.icons", service.CommittedIconTheme);
    }

    /// <summary>A broken icon theme is skipped without costing the color theme.</summary>
    [Fact]
    public void BrokenIconThemeKeepsTheColors()
    {
        var service = Service();
        service.Register(new ThemeSource("""
            @const icon-theme-id = "test.broken";
            @import "missing.sheet";
            """));
        service.ApplyColorTheme("gaya.nord");
        service.ApplyIconTheme("test.broken");

        Assert.Equal("gaya.nord", service.Current.Id);
        Assert.Contains("missing.sheet", service.Diagnostic!.Message);
    }

    /// <summary>A look that appears with a brick is applied once its folder is scanned.</summary>
    [Fact]
    public void PersistedLookAppliesWhenItsBrickAppears()
    {
        var service = Service();
        service.ApplyLook("test.square");
        Assert.Equal("test.square", service.CommittedLook);
        Assert.Null(service.Look);

        var brick = Path.Combine(directory, "brick", "Themes");
        Directory.CreateDirectory(brick);
        File.WriteAllText(Path.Combine(brick, "square.pss"), Square);
        service.SetBrickFolders([brick]);

        Assert.NotNull(service.Look);
        var info = Assert.Single(service.Looks, look => look.Id == "test.square");
        Assert.Equal(ThemeOrigin.Brick, info.Origin);
        Assert.Equal("test.night", info.DefaultDarkTheme);
        Assert.Equal("test.day", info.DefaultLightTheme);
    }

    /// <summary>A committed look and icon theme are written to the appearance page and read back after a restart.</summary>
    [Fact]
    public void WorkbenchRemembersLookAndIconTheme()
    {
        using var frame = new GayaChromeTests.Frame();
        frame.Themes.Register(new ThemeSource(Square));
        frame.Themes.Register(new ThemeSource(Icons("test.icons")));

        frame.Themes.ApplyLook("test.square");
        frame.Themes.ApplyIconTheme("test.icons");

        Assert.Equal("test.square", frame.Workbench.Appearance.Look);
        Assert.Equal("test.icons", frame.Workbench.Appearance.IconTheme);

        frame.Workbench.Appearance.Look = ThemeCatalog.DefaultLook;
        frame.Settings.NotifyChanged(AppearanceSettings.PageId);
        Assert.Equal(ThemeCatalog.DefaultLook, frame.Themes.CommittedLook);
    }

    /// <summary>Verifying a look checks that it is a complete base sheet.</summary>
    [Fact]
    public void VerifierRequiresACompleteBaseSheet()
    {
        var folder = Path.Combine(directory, "verify");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "square.pss"), Square);
        File.WriteAllText(Path.Combine(folder, "partial.pss"), "@const look-id = \"test.partial\";\nbutton { border-radius = 0; }");

        var findings = ThemeVerifier.Verify(folder);

        Assert.All(findings, finding => Assert.EndsWith("partial.pss", finding.Source));
        Assert.Contains(findings, finding => finding.Severity == ThemeFindingSeverity.Error);
    }

    /// <summary>The glyph the service's sheets give the <c>test.id</c> icon, or null when no icon theme defines it.</summary>
    static string? Glyph(ThemeService service)
    {
        var sheets = new StyleSheetCollection();
        foreach (var sheet in service.Sheets) sheets.Add(sheet);
        return sheets.Resolve(new StyleTarget("icon", "test.id", [])).Get("glyph");
    }

    ThemeService Service()
    {
        var catalog = new ThemeCatalog(NullLogger.Instance, UserFolder);
        return new ThemeService(NullLogger.Instance, catalog, Path.Combine(directory, "theme.user.pss"));
    }

    static string ThemeSheet(string id, string kind) => $"""
        @const theme-id = "{id}";
        @const theme-kind = {kind};
        @import "gaya.base";
        """;

    static string Icons(string id) => $$"""
        @const icon-theme-id = "{{id}}";
        @const icon-theme-name = "Test";
        icon#test\.id { glyph = "x"; }
        """;
}
