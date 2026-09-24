namespace Turian.Engine.Core;

/// <summary>
/// Marker interface for an asset preview provider. Implementations also implement
/// <see cref="ITexturePreviewProvider"/> or <see cref="IScenePreviewProvider"/>, whichever matches how
/// the asset is best previewed, and are discovered by the editor through <c>AssetPreviewCatalog</c>
/// via <see cref="AssetPreviewAttribute"/>.
/// </summary>
/// <remarks>
/// Implementations may live in user assemblies: apply <see cref="AssetPreviewAttribute"/> to register
/// a provider for an asset type.
/// </remarks>
public interface IAssetPreviewProvider;
