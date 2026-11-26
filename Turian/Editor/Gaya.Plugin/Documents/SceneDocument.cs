namespace Gaya.Plugin.Turian;

/// <summary>
/// An open workspace tab as a Gaya document: the scene, prefab or temporary scene behind an <see cref="AssetWrapper"/>.
/// The id is the asset id the tab strip keys its tabs by, and the title follows the wrapper as the asset is renamed.
/// </summary>
sealed class SceneDocument : IDocument
{
    /// <summary>The id of the one document kind every workspace tab belongs to.</summary>
    public const string KindId = "turian.scene";

    /// <summary>Adapts an open workspace tab.</summary>
    /// <param name="wrapper">The tab; it must wrap an asset.</param>
    public SceneDocument(AssetWrapper wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        Wrapper = wrapper;
        AssetId = wrapper.Asset?.Id ?? throw new ArgumentException("The tab holds no asset.", nameof(wrapper));
        Id = AssetId.ToString();
    }

    /// <summary>The workspace tab this document stands for.</summary>
    public AssetWrapper Wrapper { get; }

    /// <summary>The asset id behind <see cref="Id"/>.</summary>
    public Guid AssetId { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Kind => KindId;

    /// <inheritdoc />
    public string Title => Wrapper.Title;

    /// <summary>The kind every scene tab is registered under, docking Scene, Scene Tree and Game as the Studio always has.</summary>
    public static DocumentKind SceneKind { get; } = new(KindId, "Scene")
    {
        Placements = new Dictionary<string, PanelPlacement>
        {
            [GayaPlugin.ViewportPanelId] = PanelPlacement.Center,
            [GayaPlugin.SceneTreePanelId] = PanelPlacement.Left,
            [GayaPlugin.GamePanelId] = PanelPlacement.Center,
        },
    };
}
