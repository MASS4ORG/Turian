namespace Turian.Tests;

/// <summary>Exercises document previews with stylesheets, relative assets and JSON binding contexts.</summary>
public sealed class UiManagerFactoryTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("turian-ui-preview-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>Preview JSON supports nested objects, lists, numbers, booleans and null values.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderPng_BindsJson(bool insideAssets)
    {
        var folder = insideAssets ? Path.Combine(directory, "Assets", "UI") : directory;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "panel.ui");
        File.WriteAllText(path, """
            <UI>
              <Style src="theme.uss" /><Style src="missing.uss" />
              <VisualElement style="flex-grow: 1; background-color: {Panel.Color}">
                <Repeat items="{Rows}" as="row"><Label text="{row}" /></Repeat>
              </VisualElement>
            </UI>
            """);
        File.WriteAllText(Path.Combine(folder, "theme.uss"), "Label { font-size: 10; }");
        var png = new UiManagerFactory().RenderPng(path, 100, 100, """
            { "Panel": { "Color": "#ff0000" }, "Rows": ["text", 1, 0.25, true, false, null] }
            """);
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(100, bitmap.Width);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(90, 90));
    }

    /// <summary>Preview documents can render without a JSON binding context.</summary>
    [Fact]
    public void RenderPng_WithoutData()
    {
        var path = Path.Combine(directory, "panel.ui");
        File.WriteAllText(path, "<UI><VisualElement style=\"flex-grow: 1; background-color: #00ff00\" /></UI>");
        using var bitmap = SKBitmap.Decode(new UiManagerFactory().RenderPng(path, 32, 24, null));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(16, 12));
    }
}
