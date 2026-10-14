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

    /// <summary>Database paths prefer source fonts, with imported streams available by asset id.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_DatabaseFont(bool removeSource)
    {
        var source = Path.Combine(dir, "Assets", "Fonts", "test.ttf");
        var imported = Path.Combine(dir, "test.ttf");
        File.Copy(source, imported);
        var database = new AssetDatabase();
        var asset = new UiFontAsset { Id = Guid.NewGuid(), RelativePath = source };
        Assert.True(database.RegisterAsset(asset, imported));
        if (removeSource)
        {
            File.Delete(source);
        }

        var resolver = new UiFontResolver(database);
        Assert.NotNull(resolver.Resolve("Assets\\Fonts\\test.ttf"));
        Assert.NotNull(resolver.Resolve($"ASSET://{asset.Id}"));
        Assert.Null(resolver.Resolve($"asset://{Guid.NewGuid()}"));
    }

    /// <summary>Packaged fonts resolve through content even when a conflicting source exists.</summary>
    [Fact]
    public void Resolve_PackagedFont()
    {
        var source = Path.Combine(dir, "Assets", "Fonts", "test.ttf");
        var content = new MemoryContentDirectory();
        var id = Guid.NewGuid();
        var package = new OapWriter();
        package.Add(id, File.ReadAllBytes(source), "font.ttf");
        content.Add("Content/fonts.oap", package.Serialize());
        var catalog = new AssetCatalog { Version = AssetCatalog.CurrentVersion };
        catalog.Records.Add(new AssetRecord
        {
            AssetId = id,
            AssetTypeName = typeof(UiFontAsset).FullName!,
            SourceRelativePath = "Assets/Fonts/test.ttf",
            ImportedRelativePath = "Content/fonts.oap",
            PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(id),
            StorageKind = AssetStorageKind.Oap,
        });
        content.Add(AssetDatabase.RuntimeCatalogPath, Serializer.Serialize(catalog));
        var database = new AssetDatabase();
        database.LoadRuntimeCatalog(content);

        Assert.NotNull(new UiFontResolver(database).Resolve("Assets/Fonts/test.ttf"));
    }

    /// <summary>Missing font artifacts return null rather than breaking a frame.</summary>
    [Fact]
    public void Resolve_MissingArtifact()
    {
        var source = Path.Combine(dir, "Assets", "Fonts", "bad.ttf");
        File.WriteAllText(source, "invalid font");
        var database = new AssetDatabase();
        var asset = new UiFontAsset { Id = Guid.NewGuid(), RelativePath = source };
        Assert.True(database.RegisterAsset(asset, source));
        var resolver = new UiFontResolver(database, "", Path.Combine(dir, "missing"));
        File.Delete(source);
        Assert.Null(resolver.Resolve("Assets/Fonts/bad.ttf"));
        Assert.Null(resolver.Resolve($"asset://{asset.Id}"));
        Assert.Null(new UiFontResolver(null).Resolve($"asset://{asset.Id}"));
    }
}
