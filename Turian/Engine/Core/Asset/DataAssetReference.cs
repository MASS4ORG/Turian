namespace Turian.Engine.Core;

/// <summary>
/// Typed reference to a <see cref="DataAssetAsset"/> whose payload is a <typeparamref name="TData"/>. The
/// serialized form is identical to <see cref="AssetReference{DataAssetAsset}"/> — just the asset id.
/// </summary>
/// <typeparam name="TData">The payload type the referenced asset is expected to hold.</typeparam>
public sealed class DataAssetReference<TData> : AssetReference<DataAssetAsset>
    where TData : DataAsset
{
    /// <summary>
    /// Initializes a new empty data-asset reference.
    /// </summary>
    public DataAssetReference()
    {
    }

    /// <summary>
    /// Initializes a new data-asset reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The referenced asset identifier.</param>
    public DataAssetReference(Guid assetId)
        : base(assetId)
    {
    }

    /// <summary>
    /// Initializes a new data-asset reference pointing to the specified asset.
    /// </summary>
    /// <param name="asset">The referenced asset metadata.</param>
    public DataAssetReference(DataAssetAsset asset)
        : base(asset)
    {
    }

    /// <summary>
    /// Loads the referenced payload through the provided loader. Every reference to the same asset
    /// resolves to the same shared instance; use <see cref="DataAsset.Instantiate{T}"/> for a private copy.
    /// </summary>
    /// <param name="loader">The asset loader that owns the shared instance.</param>
    /// <returns>The payload, or <see langword="null"/> when empty, unresolved or of another type.</returns>
    public Task<TData?> LoadContentAsync(IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return IsEmpty ? Task.FromResult<TData?>(null) : loader.LoadContentAsync<TData>(AssetId);
    }

    /// <summary>
    /// Creates a data-asset reference pointing to the specified asset.
    /// </summary>
    /// <param name="asset">The asset to reference.</param>
    /// <returns>A new data-asset reference.</returns>
    public new static DataAssetReference<TData> From(DataAssetAsset asset) => new(asset);

    /// <summary>
    /// Creates a data-asset reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier to reference.</param>
    /// <returns>A new data-asset reference.</returns>
    public new static DataAssetReference<TData> From(Guid assetId) => new(assetId);
}
