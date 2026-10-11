namespace Turian.Tests;

/// <summary>Checks the built-in icon themes: every id resolves, themes swap and partial themes fall back to mono.</summary>
[Collection(SerialTests.Name)]
public sealed class ThemeIconTests
{
    /// <summary>Both built-in themes define every studio icon id with a loadable image.</summary>
    [Theory]
    [InlineData("gaya.icons.mono")]
    [InlineData("gaya.icons.color")]
    public void BuiltInThemesDefineEveryIcon(string theme)
    {
        using var frame = Frame(theme);

        foreach (var id in Icons.All)
            Assert.True(frame.Gui.ResolveIcon(id) is { Kind: IconKind.Picture }, $"{theme} lacks {id}");
    }

    /// <summary>Mono icons take the text color; the colored theme keeps the true colors of asset icons.</summary>
    [Fact]
    public void ColoredThemeKeepsAssetColorsAndMonoTints()
    {
        using var mono = Frame("gaya.icons.mono");
        using var colored = Frame("gaya.icons.color");

        Assert.True(mono.Gui.ResolveIcon(Icons.AssetFolder)!.Tintable);
        Assert.False(colored.Gui.ResolveIcon(Icons.AssetFolder)!.Tintable);
        Assert.NotSame(mono.Gui.ResolveIcon(Icons.AssetScene), colored.Gui.ResolveIcon(Icons.AssetScene));
        Assert.True(colored.Gui.ResolveIcon(Icons.PlayStop)!.Tintable);
        Assert.Equal(colored.Themes.Current.Error, colored.Gui.ResolveIcon(Icons.PlayStop)!.Color);
    }

    /// <summary>An icon theme that defines one id still gets every other icon from the mono theme.</summary>
    [Fact]
    public void PartialThemeFallsBackToMono()
    {
        using var frame = new GayaChromeTests.Frame();
        frame.Themes.Register(new ThemeSource("""
            @const icon-theme-id = "test.partial";
            @const icon-theme-name = "Partial";
            icon#asset\.folder { glyph = "F"; src = none; }
            """));
        frame.Themes.ApplyIconTheme("test.partial");
        frame.Draw();

        Assert.Equal(IconKind.Glyph, frame.Gui.ResolveIcon(Icons.AssetFolder)!.Kind);
        Assert.Equal(IconKind.Picture, frame.Gui.ResolveIcon(Icons.AssetScene)!.Kind);
    }

    /// <summary>An id no theme defines resolves to nothing, so the call site only reserves its square.</summary>
    [Fact]
    public void UnknownIconResolvesToNothing()
    {
        using var frame = Frame("gaya.icons.mono");
        Assert.Null(frame.Gui.ResolveIcon("test.unknown"));
    }

    /// <summary>The editor core names asset icons with plain strings; each must be an id the themes define.</summary>
    [Fact]
    public void EditorCoreAssetIconsAreThemeIds()
    {
        Assert.Equal(
            [Icons.AssetFile, Icons.AssetScene, Icons.AssetMaterial, Icons.AssetData, Icons.AssetImage,
                Icons.AssetModel, Icons.AssetScript, Icons.AssetText, Icons.AssetUiDocument, Icons.AssetStyleSheet],
            [EditorIcons.File, EditorIcons.Scene, EditorIcons.Material, EditorIcons.Data, EditorIcons.Image,
                EditorIcons.Model, EditorIcons.Script, EditorIcons.Text, EditorIcons.UiDocument,
                EditorIcons.StyleSheet]);
        Assert.All(new AssetTypeCatalog().Types, type => Assert.Contains(type.DefaultIcon, Icons.All));
    }

    static GayaChromeTests.Frame Frame(string theme)
    {
        var frame = new GayaChromeTests.Frame();
        frame.Themes.ApplyIconTheme(theme);
        frame.Draw();
        return frame;
    }
}
