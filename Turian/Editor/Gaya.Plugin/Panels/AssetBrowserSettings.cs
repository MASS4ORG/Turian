namespace Gaya.Plugin.Turian;

/// <summary>Persistent display options for the Asset Browser.</summary>
[EditorSetting("Asset Browser", Description = "Controls how assets are displayed.")]
public sealed class AssetBrowserSettings
{
    /// <summary>The settings page this object backs, for raise-changed notifications.</summary>
    public const string PageId = "gaya.turian.assetBrowser";

    /// <summary>Whether file extensions are included in asset tree labels.</summary>
    [EditorSetting("Show file extensions", Description = "Display extensions in the asset tree.")]
    public bool ShowFileExtensions { get; set; } = true;
}
