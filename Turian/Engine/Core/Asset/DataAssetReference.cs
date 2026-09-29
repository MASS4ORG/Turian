namespace Turian.Engine.Core;

/// <summary>
/// A reference to a DataAsset metadata asset whose content must be a <typeparamref name="TData"/>.
/// Serialized as the same AssetId as <see cref="AssetReference{TAsset}"/>.
/// </summary>
public sealed class DataAssetReference<TData> : AssetReference<DataAssetAsset>
    where TData : DataAsset
{
    /// <summary>Creates an empty reference.</summary>
    public DataAssetReference()
    {
    }

    /// <summary>Creates a reference to an asset id.</summary>
    public DataAssetReference(Guid assetId) : base(assetId)
    {
    }

    /// <summary>Creates a reference to an asset.</summary>
    public DataAssetReference(DataAssetAsset asset) : base(asset)
    {
    }

    /// <summary>Loads the shared payload, or null when missing or of the wrong type.</summary>
    public Task<TData?> LoadContentAsync(IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return IsEmpty ? Task.FromResult<TData?>(null) : loader.LoadContentAsync<TData>(AssetId);
    }
}
