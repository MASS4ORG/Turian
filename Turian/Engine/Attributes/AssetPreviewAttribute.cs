namespace Turian;

/// <summary>
/// Registers an <c>IAssetPreviewProvider</c> implementation for an asset type. The provider is
/// resolved by the editor to render the Inspector's mini preview and, later, asset browser thumbnails.
/// </summary>
/// <remarks>
/// The attribute is available to user code so plugins can define previews for their own asset types
/// without any editor project dependency (only the engine is referenced).
/// </remarks>
/// <param name="assetType">The asset type this provider knows how to preview.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AssetPreviewAttribute(Type assetType) : Attribute
{
    /// <summary>The asset type this provider knows how to preview.</summary>
    public Type AssetType { get; } = assetType;
}
