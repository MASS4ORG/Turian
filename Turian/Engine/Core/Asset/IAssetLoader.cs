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

    /// <summary>
    /// Loads a <c>DataAsset</c> payload's content, or returns the cached instance if already
    /// loaded. Every caller that loads the same <paramref name="dataAssetId"/> receives the same
    /// object, so mutating it is visible to everyone reading it afterward — the intended way to
    /// model shared runtime state ("SOAP variables"). Prefer <see cref="DataAsset.Instantiate"/>
    /// for per-instance data that should not be shared.
    /// </summary>
    /// <remarks>
    /// This is a separate cache from <see cref="LoadAsync{TAsset}"/>'s: that one resolves the
    /// <c>DataAssetAsset</c> metadata (its path and id), this one resolves the payload the metadata
    /// points at. <c>DataAssetAsset.GetContent</c> called directly bypasses this cache entirely and
    /// always returns a fresh, independent instance — use it only where sharing is not wanted.
    /// </remarks>
    /// <param name="dataAssetId">The identifier of the <c>DataAssetAsset</c> metadata asset.</param>
    Task<DataAsset?> LoadDataAsync(Guid dataAssetId);

    /// <summary>
    /// Returns the cached <c>DataAsset</c> content when one is available for the given identifier.
    /// Does not load it from disk.
    /// </summary>
    /// <param name="dataAssetId">The identifier of the <c>DataAssetAsset</c> metadata asset.</param>
    /// <param name="content">The cached content, or <c>null</c> when not yet loaded.</param>
    bool TryGetLoadedData(Guid dataAssetId, out DataAsset? content);

    /// <summary>
    /// Evicts every cached <c>DataAsset</c> whose declared <see cref="DataAssetPolicy"/> is not
    /// <see cref="DataAssetPolicy.Persistent"/>, so the next <see cref="LoadDataAsync"/> call
    /// re-reads it from disk. Called when Play Mode ends, to discard whatever a session mutated:
    /// the disk copy was never written to, so this is what "reset on play" restores from.
    /// </summary>
    void ReleaseAllNonPersistentData();
}
