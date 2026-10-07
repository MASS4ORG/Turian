namespace Gaya.Plugin.Turian;

/// <summary>The arrangement of the asset tree and current-folder contents.</summary>
public enum AssetBrowserViewMode
{
    /// <summary>A hierarchical asset tree.</summary>
    Tree,
    /// <summary>The tree beside a folder grid.</summary>
    Split,
    /// <summary>A folder grid navigated through breadcrumbs.</summary>
    Grid,
}

/// <summary>Persistent display options for the Asset Browser.</summary>
[EditorSetting("Asset Browser")]
public sealed class AssetBrowserSettings
{
    /// <summary>The settings page this object backs, for raise-changed notifications.</summary>
    public const string PageId = "gaya.turian.assetBrowser";

    /// <summary>Whether file extensions are included in asset tree labels.</summary>
    [EditorSetting("Show file extensions", Description = "Display extensions in the asset tree.")]
    public bool ShowFileExtensions { get; set; } = false;

    /// <summary>The browser layout.</summary>
    [EditorSetting("View mode")]
    public AssetBrowserViewMode ViewMode { get; set; } = AssetBrowserViewMode.Split;

    /// <summary>The preview size in logical pixels.</summary>
    [EditorSetting("Grid zoom")]
    public int GridZoom { get; set; } = 64;

    /// <summary>Whether the tree includes a shortcut section for favorites.</summary>
    [EditorSetting("Favorites in tree")]
    public bool ShowFavoritesInTree { get; set; }
}
