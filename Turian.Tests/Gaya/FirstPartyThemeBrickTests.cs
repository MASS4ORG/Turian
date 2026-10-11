namespace Turian.Tests;

/// <summary>Checks the first-party look bricks in <c>Gaya/Bricks</c>: they verify, apply and suggest real themes.</summary>
[Collection(SerialTests.Name)]
public sealed class FirstPartyThemeBrickTests
{
    /// <summary>Each look brick passes <c>theme verify</c> with no errors.</summary>
    [Theory]
    [InlineData("org.mass4.gaya.look-win9x")]
    [InlineData("org.mass4.gaya.look-winxp")]
    public void BricksVerifyWithoutErrors(string brick)
    {
        var findings = ThemeVerifier.Verify(BrickFolder(brick));

        Assert.DoesNotContain(findings, finding => finding.Severity == ThemeFindingSeverity.Error);
    }

    /// <summary>A look from a brick applies as the base sheet and pairs with its own dark and light themes.</summary>
    [Theory]
    [InlineData("org.mass4.gaya.look-win9x", "org.mass4.look.win9x", "org.mass4.theme.win-classic")]
    [InlineData("org.mass4.gaya.look-winxp", "org.mass4.look.winxp", "org.mass4.theme.luna")]
    public void LooksApplyAndPairWithTheirThemes(string brick, string look, string darkTheme)
    {
        var service = new ThemeService(NullLogger.Instance,
            new ThemeCatalog(NullLogger.Instance, Path.Combine(Path.GetTempPath(), $"no-themes-{Guid.NewGuid():N}")),
            Path.Combine(Path.GetTempPath(), $"no-user-{Guid.NewGuid():N}.pss"));
        service.SetBrickFolders([Path.Combine(BrickFolder(brick), "Themes")]);

        service.ApplyLook(look);

        Assert.NotNull(service.Look);
        Assert.Null(service.Diagnostic);
        Assert.Equal(darkTheme, service.DefaultColorThemeFor(look));
        Assert.All(service.Looks.Concat(service.ColorThemes), info => Assert.NotNull(info));
        ThemeTokens.Current = ThemeTokens.Default;
    }

    /// <summary>The studio's catalog offers the look bricks as built-in, and adding one needs no source.</summary>
    [Fact]
    public void StudioCatalogOffersTheBricksAsBuiltIn()
    {
        var packages = Path.GetDirectoryName(BrickFolder("org.mass4.gaya.look-winxp"))!;
        var studio = new StudioBricks(Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}"),
            PackagedPlugins.DefaultHosts, builtinDirectory: packages);

        var catalog = BrickCatalog.Local([], studio.BuiltinDirectory, null, studio.ReservedCategoryPrefixes,
            PackageScope.Studio);
        var looks = BrickCatalog.Filter(catalog, BrickFilter.BuiltIn, null, BrickCategoryFilter.Looks);

        Assert.Equal(["org.mass4.gaya.look-win9x", "org.mass4.gaya.look-winxp"], looks.Select(b => b.Id).Order());
    }

    static string BrickFolder(string brick)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Turian", "Packages", brick);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException(brick);
    }
}
