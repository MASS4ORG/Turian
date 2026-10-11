namespace Turian.Tests;

/// <summary>Checks theme sheets before installation: errors, unknown tokens and contrast.</summary>
public sealed class ThemeVerifierTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-theme-verify-{Guid.NewGuid():N}");

    /// <summary>Creates the working folder.</summary>
    public ThemeVerifierTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Every built-in theme verifies without errors.</summary>
    [Fact]
    public void BuiltInsHaveNoErrors()
    {
        var catalog = new ThemeCatalog(NullLogger.Instance, Path.Combine(root, "none"));
        foreach (var theme in catalog.ColorThemes)
        {
            var path = Path.Combine(root, theme.Id + ".pss");
            File.WriteAllText(path, Resource(theme.Id));
        }

        var findings = ThemeVerifier.Verify(root);
        Assert.DoesNotContain(findings, finding => finding.Severity == ThemeFindingSeverity.Error);
    }

    /// <summary>Unknown references are errors, unknown declarations notes, and low contrast a warning.</summary>
    [Fact]
    public void ReportsTokensAndContrast()
    {
        var path = Path.Combine(root, "pale.pss");
        File.WriteAllText(path, """
            @const theme-id = "x.pale";
            @import "gaya.base";
            $ink = #303030;
            $surface-pannel = #ffffff;
            $link = $nope;
            $gap = #ffffff;
            """);

        var findings = ThemeVerifier.Verify(path);

        Assert.Contains(findings, f => f is { Severity: ThemeFindingSeverity.Error } && f.Message.Contains("$link uses an unknown token"));
        Assert.Contains(findings, f => f is { Severity: ThemeFindingSeverity.Error } && f.Message == "$gap is not a length");
        Assert.Contains(findings, f => f is { Severity: ThemeFindingSeverity.Note } && f.Message.StartsWith("$surface-pannel"));
        Assert.Contains(findings, f => f is { Severity: ThemeFindingSeverity.Warning } && f.Message.StartsWith("$ink on $surface-panel"));
        Assert.Equal(ThemeFindingSeverity.Error, findings[0].Severity);
        Assert.StartsWith($"{path}: error:", findings[0].ToString());
    }

    /// <summary>A parse error is located, a brick folder is read through its Themes folder, and contrast is WCAG's.</summary>
    [Fact]
    public void LocatesErrorsAndReadsBricks()
    {
        var brick = ContentBricks.New(root, "user.you.icons", ContentBrickKind.IconTheme, SemanticVersion.Parse("2.0.0"));
        Assert.Empty(ThemeVerifier.Verify(brick));
        File.AppendAllText(Path.Combine(brick, "Themes", "icons.pss"), "icon { glyph = mix(; }\n");
        var error = Assert.Single(ThemeVerifier.Verify(brick));
        Assert.Equal(6, error.Line);

        Assert.Equal(21d, ThemeVerifier.ContrastRatio(GuiColor.Black, GuiColor.White), 3);
        Assert.Throws<FileNotFoundException>(() => ThemeVerifier.Verify(Path.Combine(root, "missing.pss")));
        Assert.Throws<PackageException>(() => ContentBricks.New(root, "user.you.icons", ContentBrickKind.FontPack,
            SemanticVersion.Parse("2.0.0")));
        Assert.Throws<PackageException>(() => ContentBricks.New(root, "Bad Id", ContentBrickKind.Theme,
            SemanticVersion.Parse("2.0.0")));
        var fonts = ContentBricks.New(root, "user.you.mono-fonts", ContentBrickKind.FontPack, SemanticVersion.Parse("2.0.0"));
        Assert.Contains("font-pack-id", File.ReadAllText(Path.Combine(fonts, "Themes", "mono-fonts.pss")));
        Assert.Equal("gaya:font-pack", ContentBricks.Category(ContentBrickKind.FontPack));
    }

    static string Resource(string id)
    {
        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream($"Gaya.Host.Themes.{id}.pss")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
