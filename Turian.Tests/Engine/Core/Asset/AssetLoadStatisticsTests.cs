namespace Turian.Tests;

/// <summary>Checks cold-load scope accounting independently of the process-wide asset caches.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetLoadStatisticsTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Cold uploads and failed reloads are measured; provider misses and cache hits are excluded.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureAccountingExcludesCacheHits(bool baked)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var directory = Directory.CreateTempSubdirectory("turian-texture-statistics-");
        var database = new AssetDatabase();
        var asset = new TextureAsset();
        try
        {
            Assert.Null(asset.GetContent(fixture.Vulkan, database));
            Assert.Equal(default, database.LoadStatistics.Snapshot);
            var assets = Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            var path = Path.Combine(assets.FullName, baked ? "texture.amtex" : "texture.png");
            asset.RelativePath = path;
            if (baked)
                TextureBlobWriter.Save(path, new TextureBlobContent(Format.R8G8B8A8Unorm, 2, 2, false,
                    [new byte[16]]));
            else
            {
                using var bitmap = new SKBitmap(2, 2);
                bitmap.Erase(SKColors.Magenta);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, encoded.ToArray());
            }
            Assert.True(database.RegisterAsset(asset, path));
            var texture = asset.GetContent(fixture.Vulkan, database);
            Assert.NotNull(texture);
            var loaded = database.LoadStatistics.Snapshot;
            Assert.Equal(1, loaded.Textures);
            Assert.True(loaded.TextureMilliseconds > 0);
            Assert.Same(texture, asset.GetContent(fixture.Vulkan, database));
            Assert.Equal(loaded, database.LoadStatistics.Snapshot);
            TextureAsset.InvalidateCacheEntry(asset.Id);
            File.WriteAllBytes(path, [0, 1, 2, 3]);
            Assert.Null(asset.GetContent(fixture.Vulkan, database));
            Assert.Equal(2, database.LoadStatistics.Snapshot.Textures);
            Assert.True(database.LoadStatistics.Snapshot.TextureMilliseconds >= loaded.TextureMilliseconds);
        }
        finally
        {
            TextureAsset.InvalidateCacheEntry(asset.Id);
            directory.Delete(true);
        }
    }

    /// <summary>Model and texture scopes accumulate distinct wall times and attempt counts.</summary>
    [Fact]
    public void ScopeTotalsKeepModelsAndTexturesSeparate()
    {
        var statistics = new AssetLoadStatistics();
        Assert.Equal(default, statistics.Snapshot);
        using (statistics.Measure(texture: false)) { }
        using (statistics.Measure(texture: false)) { }
        using (statistics.Measure(texture: true)) { }
        var snapshot = statistics.Snapshot;
        Assert.Equal(2, snapshot.Models);
        Assert.Equal(1, snapshot.Textures);
        Assert.True(snapshot.ModelMilliseconds >= 0);
        Assert.True(snapshot.TextureMilliseconds >= 0);
    }
}
