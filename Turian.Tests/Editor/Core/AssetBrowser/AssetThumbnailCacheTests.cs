namespace Turian.Tests;

/// <summary>Checks preview scheduling, revision invalidation and image ownership without a GPU.</summary>
public sealed class AssetThumbnailCacheTests
{
    static AssetEntry Entry(string name) => new(name, false, new Asset(), "/Assets");

    /// <summary>Repeated frame requests render only once; changed import revisions produce a new image.</summary>
    [Fact]
    public void CachedPreviewsRenderOnceAndReimportsInvalidate()
    {
        var renders = 0;
        using var cache = new AssetThumbnailCache((_, size) =>
        {
            renders++;
            using var surface = SKSurface.Create(new SKImageInfo(size, size));
            return surface.Snapshot();
        });
        var entry = Entry("model.obj");
        Assert.Null(cache.RequestImage(entry, "source:settings"));
        Assert.Null(cache.RequestImage(entry, "source:settings"));
        cache.Process();
        var first = cache.RequestImage(entry, "source:settings");
        Assert.NotNull(first);
        cache.Process();
        Assert.Equal(1, renders);
        Assert.Null(cache.RequestImage(entry, "new-source:settings"));
        cache.Process();
        Assert.NotSame(first, cache.RequestImage(entry, "new-source:settings"));
        Assert.Equal(2, renders);
        cache.Clear();
        Assert.Equal(IntPtr.Zero, first.Handle);
        Assert.Equal(0, cache.Count);
    }

    /// <summary>Resolution buckets, job budgets and LRU eviction bound GPU work and native image memory.</summary>
    [Fact]
    public void BudgetsBucketsAndEvictionAreBounded()
    {
        var sizes = new List<int>();
        using var cache = new AssetThumbnailCache((_, size) =>
        {
            sizes.Add(size);
            using var bitmap = new SKBitmap(2, 2);
            return SKImage.FromBitmap(bitmap);
        }, capacity: 2);
        var first = Entry("first");
        var second = Entry("second");
        var third = Entry("third");
        cache.RequestImage(first, "", 32);
        cache.RequestImage(second, "", 100);
        cache.RequestImage(third, "", 200);
        cache.Process(1);
        var image = cache.RequestImage(first, "", 64)!;
        Assert.Single(sizes);
        cache.Process();
        Assert.Equal([64, 128, 256], sizes);
        Assert.Equal(2, cache.Count);
        Assert.NotEqual(IntPtr.Zero, image.Handle);
        cache.BeginFrame();
        Assert.Equal(IntPtr.Zero, image.Handle);
        cache.RequestImage(first, "", 64);
        cache.Clear();
        cache.Process();
        Assert.Equal(3, sizes.Count);
    }

    /// <summary>Unsupported and failed providers are cached so fallback icons do not retry every frame.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPreviewKeepsFallbackWithoutRepeatedWork(bool throws)
    {
        var calls = 0;
        using var cache = new AssetThumbnailCache((_, _) =>
        {
            calls++;
            if (throws) throw new IOException("unavailable");
            return null;
        });
        var entry = Entry("script.cs");
        cache.RequestImage(entry, "");
        cache.Process();
        Assert.Null(cache.RequestImage(entry, ""));
        cache.Process();
        Assert.Equal(1, calls);
    }
}
