namespace Turian.Tests;

/// <summary>Checks static browser thumbnails through real texture and scene preview providers.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetThumbnailRendererTests(VulkanFixture fixture) : IClassFixture<VulkanFixture>
{
    /// <summary>Built-in texture previews are bounded, cached and use type icons for unsupported assets.</summary>
    [Fact]
    public void TexturePreviewIsDecodedAndBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"turian-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "image.png");
            using (var bitmap = new SKBitmap(400, 200))
            {
                bitmap.Erase(SKColors.Red);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            }
            using var build = new BuildManager(new AppSettings { ProjectAbsoluteDir = directory }, NullLogger.Instance);
            using var renderer = new AssetThumbnailRenderer(null!, new AssetDatabase(), new AssetPreviewCatalog(build));
            using var cache = new AssetThumbnailCache(renderer.Render);
            var entry = new AssetEntry(path, false, new TextureAsset(), directory);
            cache.RequestImage(entry, "");
            cache.Process();
            var image = cache.RequestImage(entry, "")!;
            Assert.NotNull(image);
            Assert.InRange(image.Width, 1, 128);
            Assert.InRange(image.Height, 1, 128);
            Assert.True(image.Width > image.Height);
            Assert.Null(renderer.Render(entry with { AssetMetadata = null }, 128));
            Assert.Null(renderer.Render(entry with { AssetMetadata = new Asset() }, 128));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A scene provider renders once per revision and its owned resources are disposed.</summary>
    [Fact]
    public void SceneProviderRendersOnceAndHonorsCustomTextureProviders()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var directory = Path.Combine(Path.GetTempPath(), $"turian-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var build = new BuildManager(new AppSettings { ProjectAbsoluteDir = directory }, NullLogger.Instance);
            var catalog = new AssetPreviewCatalog(build);
            using var renderer = new AssetThumbnailRenderer(fixture.Vulkan, new AssetDatabase(), catalog);
            using var cache = new AssetThumbnailCache(renderer.Render);
            SceneProvider.Builds = 0;
            SceneProvider.Disposals = 0;
            var entry = new AssetEntry("model.obj", false, new ThumbnailSceneAsset(), directory);
            cache.RequestImage(entry, "first");
            cache.Process();
            Assert.NotNull(cache.RequestImage(entry, "first"));
            cache.Process();
            Assert.Equal(1, SceneProvider.Builds);
            Assert.Equal(1, SceneProvider.Disposals);
            cache.RequestImage(entry, "second", 256);
            cache.Process();
            Assert.Equal(2, SceneProvider.Builds);
            Assert.Equal(2, SceneProvider.Disposals);
            CustomTextureProvider.Calls = 0;
            using var custom = renderer.Render(entry with { AssetMetadata = new ThumbnailTextureAsset() }, 64);
            Assert.Null(custom);
            Assert.Equal(1, CustomTextureProvider.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A counted scene provider with an owned preview resource.</summary>
    [AssetPreview(typeof(ThumbnailSceneAsset))]
    public sealed class SceneProvider : IScenePreviewProvider
    {
        /// <summary>Number of preview scenes produced.</summary>
        public static int Builds { get; set; }

        /// <summary>Number of preview resources released.</summary>
        public static int Disposals { get; set; }

        /// <inheritdoc />
        public AssetPreviewScene BuildPreview(Asset asset, Vulkan vulkan, AssetDatabase assets)
        {
            Builds++;
            return new AssetPreviewScene(new Node(), new Bounds(new Vector3(-1), new Vector3(1)), new Resource());
        }

        sealed class Resource : IDisposable
        {
            public void Dispose() => Disposals++;
        }
    }

    /// <summary>A custom texture provider that replaces the source-file thumbnail.</summary>
    [AssetPreview(typeof(ThumbnailTextureAsset))]
    public sealed class CustomTextureProvider : ITexturePreviewProvider
    {
        /// <summary>Number of provider invocations.</summary>
        public static int Calls { get; set; }

        /// <inheritdoc />
        public Texture? GetPreviewTexture(Asset asset, Vulkan vulkan, AssetDatabase assets)
        {
            Calls++;
            return null;
        }
    }

    /// <summary>An asset with a counted scene preview.</summary>
    public sealed class ThumbnailSceneAsset : Asset;

    /// <summary>A texture asset with a custom provider instead of a source-file preview.</summary>
    public sealed class ThumbnailTextureAsset : TextureAsset;
}
