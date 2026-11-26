namespace Gaya.Plugin.Turian;

/// <summary>
/// An open workspace tab that is not a scene, such as a material. Its kind has no document-bound panels, so the
/// workbench shows the global panels only and the Inspector shows the asset.
/// </summary>
sealed class AssetDocument : IDocument
{
    /// <summary>The id of the kind every non-scene workspace tab belongs to.</summary>
    public const string KindId = "turian.asset";

    /// <summary>Adapts an open workspace tab.</summary>
    /// <param name="wrapper">The tab; it must wrap an asset.</param>
    public AssetDocument(AssetWrapper wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        Wrapper = wrapper;
        Id = (wrapper.Asset ?? throw new ArgumentException("The tab holds no asset.", nameof(wrapper))).Id.ToString();
    }

    /// <summary>The workspace tab this document stands for.</summary>
    public AssetWrapper Wrapper { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Kind => KindId;

    /// <inheritdoc />
    public string Title => Wrapper.Title;

    /// <summary>The kind of every non-scene tab: it owns no panels.</summary>
    public static DocumentKind AssetKind { get; } = new(KindId, "Asset");

    /// <summary>Adapts a workspace tab as the document its asset calls for: a scene for prefabs, an asset otherwise.</summary>
    /// <param name="tab">The tab; it must wrap an asset.</param>
    public static IDocument For(AssetWrapper tab) => tab.Asset is Prefab ? new SceneDocument(tab) : new AssetDocument(tab);
}
