namespace Turian.Tests;

/// <summary>Tests for <see cref="UiFontResolver"/> — path / family-name resolution and caching.</summary>
public sealed class UiFontResolverTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"ui-font-{Guid.NewGuid():N}");
    /// <summary>Creates a temp folder with a real TTF copied from the platform's default typeface.</summary>
    public UiFontResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Assets", "Fonts"));
        var fontPath = Path.Combine(dir, "Assets", "Fonts", "test.ttf");

        // Write a real TTF payload — the platform's default typeface.
        using var typeface = SKTypeface.FromFamilyName(null);
        using var asset = typeface.OpenStream();
        var bytes = new byte[asset.Length];
        asset.Read(bytes, bytes.Length);
        File.WriteAllBytes(fontPath, bytes);
    }

    /// <summary>Deletes the temp folder, ignoring cleanup races.</summary>
    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A relative <c>.ttf</c> path resolves to a loaded font.</summary>
    [Fact]
    public void Resolve_RelativeTtfPath_LoadsFont()
    {
        var resolver = new UiFontResolver(database: null, dir);

        var font = resolver.Resolve("Assets/Fonts/test.ttf");

        Assert.NotNull(font);
    }

    /// <summary>Quotes around a path are trimmed before resolution.</summary>
    [Fact]
    public void Resolve_QuotedPath_IsTrimmed()
    {
        var resolver = new UiFontResolver(database: null, dir);

        Assert.NotNull(resolver.Resolve("\"Assets/Fonts/test.ttf\""));
    }

    /// <summary>Repeated resolution of the same path returns the same cached instance.</summary>
    [Fact]
    public void Resolve_CachesByReference()
    {
        var resolver = new UiFontResolver(database: null, dir);

        Assert.Same(resolver.Resolve("Assets/Fonts/test.ttf"), resolver.Resolve("Assets/Fonts/test.ttf"));
    }

    /// <summary>Missing files and empty paths resolve to null.</summary>
    [Fact]
    public void Resolve_MissingPath_ReturnsNull()
    {
        var resolver = new UiFontResolver(database: null, dir);

        Assert.Null(resolver.Resolve("Assets/Fonts/missing.ttf"));
        Assert.Null(resolver.Resolve(""));
    }

    /// <summary>A bare font name resolves through the family lookup.</summary>
    [Fact]
    public void Resolve_BareName_UsesFamilyLookup()
    {
        var resolver = new UiFontResolver(database: null, dir);

        // A family name always yields a font (Skia falls back to a default face).
        Assert.NotNull(resolver.Resolve("sans-serif"));
    }
}
