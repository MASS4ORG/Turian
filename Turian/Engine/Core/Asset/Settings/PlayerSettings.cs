namespace Turian.Engine.Core;

/// <summary>What a built game is called, who ships it, and the scene it starts in.</summary>
[CreateAssetMenu(fileName: "PlayerSettings", path: "Settings/Player Settings")]
[TypeId("a3000005-0000-4000-8000-000000000001")]
public class PlayerSettings : ProjectSettingsAsset
{
    /// <summary>Product title shown to players in builds and runtime windows.</summary>
    public string? ProductName { get; set; }

    /// <summary>Company or studio name associated with the project.</summary>
    public string? Author { get; set; }

    /// <summary>Reverse-DNS style application identifier, such as com.company.game.</summary>
    public string? ApplicationIdentifier { get; set; }

    /// <summary>Semantic version string for the project, such as 0.1.0.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// The image the game is known by: the executable's icon, its window's icon, and what the studio shows
    /// beside the project in its recent-projects list. Any image asset works; it is scaled to icon sizes
    /// when the game is built.
    /// </summary>
    public AssetReference<TextureAsset>? Icon { get; set; }

    /// <summary>The file a build writes the icon to beside the game, which the game's window loads.</summary>
    public const string BuiltIconFileName = "icon.png";

    /// <summary>The first scene that should be loaded when the game starts.</summary>
    public AssetReference<Prefab>? StartupScene { get; set; }
}
