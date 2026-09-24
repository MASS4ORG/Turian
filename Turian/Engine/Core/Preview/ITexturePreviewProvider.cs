namespace Turian.Engine.Core;

/// <summary>
/// An asset preview that is itself a texture — no 3D scene needed. The engine composites the result
/// over an empty frame with <c>SceneViewerService.OverlayTexture</c>, the same mechanism the
/// screen-space GUI uses to blit onto the viewport.
/// </summary>
public interface ITexturePreviewProvider : IAssetPreviewProvider
{
    /// <summary>
    /// Gets the texture to display for <paramref name="asset"/>, or <c>null</c> when it cannot be
    /// resolved. The caller does not own the returned texture's lifetime.
    /// </summary>
    /// <param name="asset">The asset to preview.</param>
    /// <param name="vulkan">The Vulkan context to resolve GPU resources against.</param>
    Texture? GetPreviewTexture(Asset asset, Vulkan vulkan);
}
