namespace Turian.Editor.Core;

/// <summary>
/// Previews a <see cref="TextureAsset"/> by showing the decoded texture itself — no scene needed.
/// </summary>
[AssetPreview(typeof(TextureAsset))]
public sealed class TextureAssetPreviewProvider : ITexturePreviewProvider
{
    /// <inheritdoc/>
    public Texture? GetPreviewTexture(Asset asset, Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        return asset is TextureAsset texture ? texture.GetContent(vulkan) : null;
    }
}
