namespace Turian.Engine.Core;

/// <summary>
/// An asset preview that needs an actual lit 3D render — a model, or a material shown on a swatch.
/// The engine renders the returned scene with <c>SceneViewerService.Render</c>, framed by
/// <see cref="AssetPreviewScene.Bounds"/>, the same way the Scene View renders the edited hierarchy.
/// </summary>
public interface IScenePreviewProvider : IAssetPreviewProvider
{
    /// <summary>
    /// Builds a self-contained scene — mesh and light nodes as children of
    /// <see cref="AssetPreviewScene.Root"/> — to render <paramref name="asset"/>.
    /// </summary>
    /// <param name="asset">The asset to preview.</param>
    /// <param name="vulkan">The Vulkan context to build GPU resources against.</param>
    AssetPreviewScene BuildPreview(Asset asset, Vulkan vulkan);
}
