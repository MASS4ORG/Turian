namespace Turian.Editor.Core;

/// <summary>Renders provider previews through one reusable offscreen viewer.</summary>
public sealed class AssetThumbnailRenderer(Vulkan vulkan, AssetDatabase assets, AssetPreviewCatalog catalog) : IDisposable
{
    SceneViewerService? viewer;
    readonly Node emptyRoot = new() { Name = "AssetThumbnail" };

    /// <summary>Renders a static thumbnail, honoring custom texture and scene preview providers.</summary>
    public SKImage? Render(AssetEntry entry, int size)
    {
        if (entry.AssetMetadata is not { } asset) return null;
        var provider = catalog.GetProvider(asset.GetType());
        if (provider is null) return null;
        if (provider.GetType() == typeof(TextureAssetPreviewProvider) && Decode(entry.AbsolutePath, size) is { } decoded)
            return decoded;
        EnsureViewer(size);
        return RenderProvider(asset, provider);
    }

    void EnsureViewer(int size)
    {
        if (viewer is not null && viewer.Width == size) return;
        viewer?.Dispose();
        viewer = new SceneViewerService(vulkan, assets, (uint)size, (uint)size);
    }

    SKImage? RenderProvider(Asset asset, IAssetPreviewProvider provider)
    {
        var service = viewer!;
        if (!SetOverlay(asset, provider)) return null;
        AssetPreviewScene? scene = null;
        try
        {
            scene = BuildScene(asset, provider);
            service.Render(scene?.Root ?? emptyRoot, 0);
            var pixels = new byte[service.Width * service.Height * 4];
            service.CopyPixels(pixels);
            return Snapshot(pixels, (int)service.Width, (int)service.Height);
        }
        finally { scene?.OwnedResources?.Dispose(); }
    }

    bool SetOverlay(Asset asset, IAssetPreviewProvider provider)
    {
        viewer!.OverlayTexture = provider is ITexturePreviewProvider texture
            ? texture.GetPreviewTexture(asset, vulkan, assets) : null;
        return provider is not ITexturePreviewProvider || viewer.OverlayTexture is not null;
    }

    AssetPreviewScene? BuildScene(Asset asset, IAssetPreviewProvider provider)
    {
        if (provider is not IScenePreviewProvider sceneProvider) return null;
        var scene = sceneProvider.BuildPreview(asset, vulkan, assets);
        viewer!.FrameBounds(scene.Bounds);
        return scene;
    }

    static SKImage? Decode(string path, int size)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var scale = Math.Min(1f, size / (float)Math.Max(codec.Info.Width, codec.Info.Height));
        var dimensions = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(dimensions.Width, dimensions.Height);
        using var bitmap = SKBitmap.Decode(codec, info);
        if (bitmap is null) return null;
        var target = new SKImageInfo(Math.Max(1, (int)(codec.Info.Width * scale)),
            Math.Max(1, (int)(codec.Info.Height * scale)));
        using var resized = bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear));
        return resized is null ? null : SKImage.FromBitmap(resized);
    }

    static SKImage Snapshot(byte[] pixels, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally { handle.Free(); }
    }

    /// <inheritdoc />
    public void Dispose() => viewer?.Dispose();
}
