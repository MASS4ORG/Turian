namespace Turian.Tests;

/// <summary>Tests for <see cref="UiImageResolver"/> — path / <c>asset://</c> resolution and caching.</summary>
public sealed class UiImageResolverTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"ui-img-{Guid.NewGuid():N}");

    /// <summary>Creates a temp folder with a generated PNG to resolve against.</summary>
    public UiImageResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Assets", "Textures"));
        WritePng(Path.Combine(dir, "Assets", "Textures", "button.png"), 8, 8);
    }

    /// <summary>Deletes the temp folder, ignoring cleanup races.</summary>
    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    static void WritePng(string path, int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>A relative path against the root folder decodes to an image of the right size.</summary>
    [Fact]
    public void Resolve_RelativePath_AgainstRoot_DecodesImage()
    {
        var resolver = new UiImageResolver(database: null, dir);

        var image = resolver.Resolve("Assets/Textures/button.png");

        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
    }

    /// <summary>Repeated resolution of the same path returns the same cached instance.</summary>
    [Fact]
    public void Resolve_CachesByReference()
    {
        var resolver = new UiImageResolver(database: null, dir);

        var a = resolver.Resolve("Assets/Textures/button.png");
        var b = resolver.Resolve("Assets/Textures/button.png");

        Assert.Same(a, b);
    }

    /// <summary>Empty paths, missing files and bad asset ids resolve to null.</summary>
    [Fact]
    public void Resolve_MissingOrEmpty_ReturnsNull()
    {
        var resolver = new UiImageResolver(database: null, dir);

        Assert.Null(resolver.Resolve(""));
        Assert.Null(resolver.Resolve("Assets/Textures/missing.png"));
        Assert.Null(resolver.Resolve("asset://not-a-guid"));
    }
}
