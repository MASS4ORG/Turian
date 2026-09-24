namespace Turian.Engine.Core;

/// <summary>
/// Represents a serializable reference to an asset stored in the asset database.
/// </summary>
/// <typeparam name="TAsset">The asset metadata type being referenced.</typeparam>
public class AssetReference<TAsset>
    where TAsset : Asset
{
    /// <summary>
    /// Gets or sets the unique identifier of the referenced asset.
    /// </summary>
    public Guid AssetId { get; set; }

    /// <summary>
    /// Gets a value indicating whether this reference does not point to any asset.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => AssetId == Guid.Empty;

    /// <summary>
    /// Gets the referenced asset identifier or <see langword="null"/> when the reference is empty.
    /// </summary>
    [JsonIgnore]
    public Guid? AssetIdOrNull => IsEmpty ? null : AssetId;

    /// <summary>
    /// Initializes a new empty asset reference.
    /// </summary>
    public AssetReference()
    {
    }

    /// <summary>
    /// Initializes a new asset reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The referenced asset identifier.</param>
    public AssetReference(Guid assetId)
    {
        AssetId = assetId;
    }

    /// <summary>
    /// Initializes a new asset reference pointing to the specified asset.
    /// </summary>
    /// <param name="asset">The referenced asset.</param>
    public AssetReference(TAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        AssetId = asset.Id;
    }

    /// <summary>
    /// Clears the current reference.
    /// </summary>
    public void Clear() => AssetId = Guid.Empty;

    /// <summary>
    /// Updates this reference to point to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier to reference.</param>
    public void Set(Guid assetId) => AssetId = assetId;

    /// <summary>
    /// Updates this reference to point to the specified asset.
    /// </summary>
    /// <param name="asset">The asset to reference.</param>
    public void Set(TAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        AssetId = asset.Id;
    }

    /// <summary>
    /// Resolves the referenced asset metadata from the given asset database.
    /// </summary>
    /// <param name="assetDatabase">The asset database used to resolve the reference.</param>
    /// <returns>
    /// The referenced asset metadata instance when found and of the expected type; otherwise, <see langword="null"/>.
    /// </returns>
    public TAsset? Resolve(AssetDatabase assetDatabase)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);

        if (IsEmpty)
        {
            return null;
        }

        if (!assetDatabase.TryGetAsset(AssetId, out var record) || record is null)
        {
            return null;
        }

        return CreateAsset(record);
    }

    /// <summary>
    /// Tries to resolve the referenced asset metadata from the given asset database.
    /// </summary>
    /// <param name="assetDatabase">The asset database used to resolve the reference.</param>
    /// <param name="asset">When this method returns, contains the resolved asset if found; otherwise, null.</param>
    /// <returns><see langword="true"/> if the asset could be resolved; otherwise, <see langword="false"/>.</returns>
    public bool TryResolve(AssetDatabase assetDatabase, out TAsset? asset)
    {
        asset = Resolve(assetDatabase);
        return asset is not null;
    }

    /// <summary>
    /// Loads the referenced asset content through the provided loader, returning the shared
    /// cached instance when available. Returns <see langword="null"/> when the reference is
    /// empty, the asset cannot be resolved, or the cached type does not match
    /// <typeparamref name="TAsset"/>.
    /// </summary>
    /// <param name="loader">The asset loader used to fetch or resolve the cached instance.</param>
    public Task<TAsset?> LoadAsync(IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return IsEmpty ? Task.FromResult<TAsset?>(null) : loader.LoadAsync<TAsset>(AssetId);
    }

    /// <summary>
    /// Returns the cached asset instance if the loader has already resolved this reference;
    /// otherwise, <see langword="null"/>.
    /// </summary>
    /// <param name="loader">The asset loader used to consult the cache.</param>
    public TAsset? GetLoaded(IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return !IsEmpty && loader.TryGetLoaded<TAsset>(AssetId, out var asset) ? asset : null;
    }

    /// <summary>
    /// Returns a string representation of this asset reference.
    /// </summary>
    /// <returns>A display-friendly string representation.</returns>
    public override string ToString() =>
        IsEmpty
            ? $"{typeof(TAsset).Name}(Empty)"
            : $"{typeof(TAsset).Name}({AssetId})";

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is AssetReference<TAsset> other && Equals(other);

    /// <summary>
    /// Determines whether this reference and another reference point to the same asset.
    /// </summary>
    /// <param name="other">The other reference to compare with.</param>
    /// <returns><see langword="true"/> when both references point to the same asset; otherwise, <see langword="false"/>.</returns>
    public bool Equals(AssetReference<TAsset>? other) =>
        other is not null && AssetId == other.AssetId;

    /// <inheritdoc />
    // Asset references are serialized as mutable IDs, so identity cannot be a readonly member.
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    public override int GetHashCode() => HashCode.Combine(typeof(TAsset), AssetId);

    /// <summary>
    /// Creates an asset reference pointing to the specified asset.
    /// </summary>
    /// <param name="asset">The asset to reference.</param>
    /// <returns>A new asset reference.</returns>
    public static AssetReference<TAsset> From(TAsset asset) => new(asset);

    /// <summary>
    /// Creates an asset reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier to reference.</param>
    /// <returns>A new asset reference.</returns>
    public static AssetReference<TAsset> From(Guid assetId) => new(assetId);

    /// <summary>
    /// Converts an asset into an asset reference.
    /// </summary>
    /// <param name="asset">The asset to reference.</param>
    public static implicit operator AssetReference<TAsset>(TAsset asset) => new(asset);

    /// <summary>
    /// Converts an asset identifier into an asset reference.
    /// </summary>
    /// <param name="assetId">The asset identifier to reference.</param>
    public static implicit operator AssetReference<TAsset>(Guid assetId) => new(assetId);

    static TAsset? CreateAsset(AssetRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AssetTypeName))
        {
            return null;
        }

        var expectedType = typeof(TAsset);

        if (!string.Equals(record.AssetTypeName, expectedType.Name, StringComparison.Ordinal)
            && !string.Equals(record.AssetTypeName, expectedType.FullName, StringComparison.Ordinal))
        {
            return null;
        }

        if (Activator.CreateInstance(expectedType) is not TAsset asset)
        {
            return null;
        }

        asset.Id = record.AssetId;
        asset.RelativePath = record.SourceRelativePath;
        return asset;
    }
}
