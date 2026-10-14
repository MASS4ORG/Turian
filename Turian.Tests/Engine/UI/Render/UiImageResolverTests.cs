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

    /// <summary>A built game resolves both path and id references from the baked texture without source files.</summary>
    [Fact]
    public void Resolve_ImportedTextureWithoutSource_PreservesPixels()
    {
        var database = new AssetDatabase();
        var source = Path.Combine(dir, "Assets", "Textures", "button.png");
        var texture = new TextureAsset { Id = Guid.NewGuid(), RelativePath = source };
        var imported = Path.Combine(dir, "imported");
        Directory.CreateDirectory(imported);
        var artifact = new TextureAssetImporter().ImportToCache(texture, source, imported)[0];
        Assert.True(database.RegisterAsset(texture, Path.Combine(imported, artifact)));
        File.Delete(source);

        var resolver = new UiImageResolver(database);
        using var byId = resolver.Resolve($"asset://{texture.Id}");
        Assert.NotNull(byId);
        Assert.Equal(8, byId.Width);
        using var bitmap = SKBitmap.FromImage(byId);
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(0, 0));
        using var byPath = resolver.Resolve("Assets/Textures/button.png");
        Assert.NotNull(byPath);
    }

    /// <summary>A live source image takes priority over its imported artifact.</summary>
    [Fact]
    public void Resolve_DatabasePathPrefersSource()
    {
        var source = Path.Combine(dir, "Assets", "Textures", "button.png");
        var imported = Path.Combine(dir, "imported.png");
        WritePng(imported, 2, 2);
        var database = new AssetDatabase();
        var asset = new TextureAsset { Id = Guid.NewGuid(), RelativePath = source };
        Assert.True(database.RegisterAsset(asset, imported));
        var resolver = new UiImageResolver(database);

        using var image = resolver.Resolve("assets\\textures\\button.png");
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
        using var byId = resolver.Resolve($"asset://{asset.Id}");
        Assert.NotNull(byId);
        Assert.Equal(2, byId.Width);
        Assert.Null(resolver.Resolve($"asset://{Guid.NewGuid()}"));
    }

    /// <summary>Packaged image paths resolve through content with no project directory.</summary>
    [Fact]
    public void Resolve_PackagedImage()
    {
        var source = Path.Combine(dir, "Assets", "Textures", "button.png");
        var content = new MemoryContentDirectory();
        var id = Guid.NewGuid();
        var package = new OapWriter();
        package.Add(id, File.ReadAllBytes(source), "image.png");
        content.Add("Content/images.oap", package.Serialize());
        var catalog = new AssetCatalog { Version = AssetCatalog.CurrentVersion };
        catalog.Records.Add(new AssetRecord
        {
            AssetId = id,
            AssetTypeName = typeof(TextureAsset).FullName!,
            SourceRelativePath = "Assets/Textures/button.png",
            ImportedRelativePath = "Content/images.oap",
            PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(id),
            StorageKind = AssetStorageKind.Oap,
        });
        content.Add(AssetDatabase.RuntimeCatalogPath, Serializer.Serialize(catalog));
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(content);

        using var image = new UiImageResolver(database).Resolve("Assets/Textures/button.png");
        Assert.NotNull(image);
        Assert.Equal(8, image.Width);
    }

    /// <summary>Invalid and missing artifacts are cached as unavailable images.</summary>
    [Fact]
    public void Resolve_UnreadableArtifact()
    {
        var source = Path.Combine(dir, "Assets", "Textures", "bad.png");
        File.WriteAllText(source, "invalid image");
        var database = new AssetDatabase();
        var asset = new TextureAsset { Id = Guid.NewGuid(), RelativePath = source };
        Assert.True(database.RegisterAsset(asset, source));
        var resolver = new UiImageResolver(database, "", Path.Combine(dir, "missing"));
        Assert.Null(resolver.Resolve("Assets/Textures/bad.png"));
        File.Delete(source);
        Assert.Null(resolver.Resolve($"asset://{asset.Id}"));
        Assert.Null(resolver.Resolve($"asset://{asset.Id}"));
    }

    /// <summary>CPU UI decoding accepts RGBA8 textures and rejects compressed GPU texture formats.</summary>
    [Theory]
    [InlineData(Ktx2VkFormat.R8G8B8A8Srgb, true)]
    [InlineData(Ktx2VkFormat.R8G8B8A8Unorm, true)]
    [InlineData(Ktx2VkFormat.Bc1RgbaUnormBlock, false)]
    public void Resolve_TextureFormats(Ktx2VkFormat format, bool supported)
    {
        var path = Path.Combine(dir, "Assets", "Textures", "texture.ktx2");
        Ktx2Writer.Save(path, new Ktx2Texture
        {
            Format = format,
            Width = 4,
            Height = 4,
            Levels = [new byte[supported ? 64 : 8]],
        });
        var database = new AssetDatabase();
        var asset = new TextureAsset { Id = Guid.NewGuid(), RelativePath = path };
        Assert.True(database.RegisterAsset(asset, path));
        using var image = new UiImageResolver(database).Resolve($"asset://{asset.Id}");
        Assert.Equal(supported, image is not null);
        if (image is not null)
        {
            Assert.Equal(4, image.Width);
        }
    }
}
