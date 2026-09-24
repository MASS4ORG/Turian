namespace Turian.Engine.Core;

/// <summary>
/// Resolves serialized asset content by identifier and guarantees that repeated requests
/// for the same asset return the same shared instance.
/// </summary>
/// <remarks>
/// The loader is the canonical owner of loaded asset instances. This is the mechanism by
/// which <c>DataAsset</c>-style singletons remain consistent across <see cref="AssetReference{TAsset}"/>
/// callers: every caller resolves the same GUID and therefore receives the same object.
/// </remarks>
public interface IAssetLoader
{
    /// <summary>
    /// Loads the asset with the given identifier, or returns the cached instance if already loaded.
    /// Returns <c>null</c> when the asset cannot be resolved or does not match the requested type.
    /// </summary>
    /// <typeparam name="TAsset">The expected asset type.</typeparam>
    /// <param name="assetId">The asset identifier.</param>
    Task<TAsset?> LoadAsync<TAsset>(Guid assetId) where TAsset : Asset;

    /// <summary>
    /// Returns the cached asset instance when one is available for the given identifier.
    /// Does not load the asset from disk.
    /// </summary>
    /// <typeparam name="TAsset">The expected asset type.</typeparam>
    /// <param name="assetId">The asset identifier.</param>
    /// <param name="asset">The cached asset, or <c>null</c> when not yet loaded or not of the expected type.</param>
    bool TryGetLoaded<TAsset>(Guid assetId, out TAsset? asset) where TAsset : Asset;

    /// <summary>
    /// Removes the cached instance for the given asset. Subsequent <see cref="LoadAsync{TAsset}"/>
    /// calls will reload from disk. Callers must ensure no live references to the released
    /// instance exist before calling this method.
    /// </summary>
    void Release(Guid assetId);
}
