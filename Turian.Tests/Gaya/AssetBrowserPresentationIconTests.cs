namespace Turian.Tests;

/// <summary>Studio brick images participate in browser icon drawing and retain a glyph fallback.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetBrowserPresentationIconTests
{
    /// <summary>Serialized payload presentations draw visible pixels in the actual tree and grid.</summary>
    [Theory]
    [InlineData(AssetBrowserViewMode.Grid)]
    [InlineData(AssetBrowserViewMode.Tree)]
    public void PayloadIconsAreVisibleInTheBrowser(AssetBrowserViewMode mode)
    {
        using var browser = new AssetBrowserHarness(mode);
        using var surface = SKSurface.Create(new SKImageInfo(16, 16));
        surface.Canvas.Clear(SKColors.Lime);
        using var icon = surface.Snapshot();
        using var data = icon.Encode(SKEncodedImageFormat.Png, 100);
        browser.Field<AssetTypeCatalog>("types").Register(new AssetTypePresentation(typeof(DataAssetTest),
            "test.payload", "Payload", "test.missing", new Color32(255, 255, 255), "Test")
        { ImageData = data.ToArray() });
        Serializer.Save(Path.Combine(browser.Root, "payload.dataasset"), new DataAssetTest());
        browser.Importer.GenerateMetaFiles(browser.Root);
        browser.Invoke("Refresh");
        browser.State.SetExpanded(browser.Root, true);
        using var captured = browser.Capture();
        using var bitmap = SKBitmap.FromImage(captured);
        Assert.True(bitmap.Pixels.Count(static pixel => pixel.Green > 220 && pixel.Red < 30 && pixel.Blue < 30) > 20,
            "The browser must draw the payload's raster icon, not just cache its decoded image.");
    }

    /// <summary>Raster icons decode once; malformed images use the contributed glyph.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PresentationIconsDecodeAndCache(bool valid)
    {
        using var browser = new AssetBrowserHarness();
        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        surface.Canvas.Clear(SKColors.Red);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var presentation = new AssetTypePresentation(typeof(Asset), "test.raster", "Raster", "test.missing",
            new Color32(255, 255, 255), "Test")
        { ImageData = valid ? data.ToArray() : [0], Glyph = "R" };
        browser.Field<AssetTypeCatalog>("types").Register(presentation);
        var entry = browser.Entry("a.txt") with { AssetMetadata = new Asset() };
        browser.Frame(gui => browser.Invoke("DrawAssetIcon", gui, entry, 48f));
        var icons = browser.Field<Dictionary<string, Icon>>("presentationIcons");
        var icon = Assert.Single(icons).Value;
        Assert.Equal(valid ? IconKind.Image : IconKind.Glyph, icon.Kind);
        browser.Frame(gui => browser.Invoke("DrawAssetIcon", gui, entry, 48f));
        Assert.Same(icon, Assert.Single(icons).Value);
    }
}
