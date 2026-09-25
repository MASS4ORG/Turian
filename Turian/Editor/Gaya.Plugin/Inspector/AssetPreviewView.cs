namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws a small render of the selected asset above its import settings. Resolves a provider from
/// <see cref="AssetPreviewCatalog"/> and draws nothing for asset types without one.
/// </summary>
sealed class AssetPreviewView(Vulkan vulkan, AssetPreviewCatalog catalog) : IDisposable
{
    const uint previewSize = 180;

    readonly Node emptyRoot = new() { Name = "__AssetPreviewEmpty" };

    SceneViewerService? service;
    byte[] pixels = [];
    SKImage? frame;

    AssetPreviewScene? scene;
    Guid sceneAssetId;
    Type? sceneProviderType;

    /// <summary>Draws the preview for <paramref name="asset"/>, or nothing when no provider matches.</summary>
    public void Draw(Gui gui, Asset? asset)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var provider = asset is null ? null : catalog.GetProvider(asset.GetType());
        if (provider is null || asset is null) return;

        using (gui.Node(previewSize, previewSize, "inspector/asset/preview").Enter())
        {
            if (gui.Pass != Pass.Pass2Render) return;

            if (!EnsureService()) return;

            var rect = gui.CurrentNode.Rect;
            Render(gui, rect, asset, provider, gui.Time.DeltaTime);
        }
    }

    bool EnsureService()
    {
        if (service is not null) return true;

        try
        {
            service = new SceneViewerService(vulkan, previewSize, previewSize);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    void Render(Gui gui, Rect rect, Asset asset, IAssetPreviewProvider provider, float deltaTime)
    {
        var svc = service!;

        svc.OverlayTexture = provider is ITexturePreviewProvider textureProvider
            ? textureProvider.GetPreviewTexture(asset, vulkan)
            : null;

        var root = emptyRoot;
        if (provider is IScenePreviewProvider sceneProvider)
        {
            if (sceneAssetId != asset.Id || sceneProviderType != provider.GetType())
            {
                scene?.OwnedResources?.Dispose();
                scene = sceneProvider.BuildPreview(asset, vulkan);
                sceneAssetId = asset.Id;
                sceneProviderType = provider.GetType();
                svc.FrameBounds(scene.Value.Bounds);
            }

            root = scene!.Value.Root;
        }

        svc.Render(root, deltaTime);

        if (pixels.Length != svc.Width * svc.Height * 4) pixels = new byte[svc.Width * svc.Height * 4];
        svc.CopyPixels(pixels);

        frame?.Dispose();
        frame = Snapshot(pixels, svc.Width, svc.Height);
        if (frame is not null) gui.DrawImage(frame, rect);
    }

    /// <summary>
    /// Wraps the read-back pixels as an image for this frame. The copy is what makes the buffer safe
    /// to overwrite on the next one; the GPU-shared path is the follow-up to this.
    /// </summary>
    static SKImage? Snapshot(byte[] buffer, uint width, uint height)
    {
        if (width == 0 || height == 0) return null;

        var info = new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        scene?.OwnedResources?.Dispose();
        frame?.Dispose();
        service?.Dispose();
    }
}
