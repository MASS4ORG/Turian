namespace Gaya.Sdk;

/// <summary>
/// The ids of the icons the studio draws, resolved through the active icon theme with <c>gui.StyledIcon</c>. A theme
/// defines an id with an <c>icon#id</c> rule (escape the dots, as in <c>icon#scene\.move</c>); one that leaves an id
/// out falls back to the built-in mono theme. Plugins add their own ids the same way, prefixed with their name.
/// </summary>
public static class Icons
{
    /// <summary>Select an object.</summary>
    public const string SceneSelect = "scene.select";

    /// <summary>Move along any axis.</summary>
    public const string SceneMove = "scene.move";

    /// <summary>Rotate a transform.</summary>
    public const string SceneRotate = "scene.rotate";

    /// <summary>Scale a transform.</summary>
    public const string SceneScale = "scene.scale";

    /// <summary>Use all transform operations.</summary>
    public const string SceneTransform = "scene.transform";

    /// <summary>Frame the selected object.</summary>
    public const string SceneFrame = "scene.frame";

    /// <summary>Snap transform edits.</summary>
    public const string SceneSnap = "scene.snap";

    /// <summary>Switch between perspective and orthographic projection.</summary>
    public const string SceneProjection = "scene.projection";

    /// <summary>A node in a scene.</summary>
    public const string SceneNode = "scene.node";

    /// <summary>Start playing.</summary>
    public const string PlayStart = "play.start";

    /// <summary>Pause playing.</summary>
    public const string PlayPause = "play.pause";

    /// <summary>Stop playing.</summary>
    public const string PlayStop = "play.stop";

    /// <summary>Play from the startup scene.</summary>
    public const string PlayRestart = "play.restart";

    /// <summary>Advance one frame.</summary>
    public const string PlayStep = "play.step";

    /// <summary>A closed fold arrow.</summary>
    public const string CaretRight = "ui.caret-right";

    /// <summary>An open fold arrow.</summary>
    public const string CaretDown = "ui.caret-down";

    /// <summary>Close or remove.</summary>
    public const string Close = "ui.close";

    /// <summary>Open elsewhere, such as in a new window.</summary>
    public const string External = "ui.external";

    /// <summary>Put a setting back to its default.</summary>
    public const string Revert = "ui.revert";

    /// <summary>Locked.</summary>
    public const string Lock = "ui.lock";

    /// <summary>Unlocked.</summary>
    public const string Unlock = "ui.unlock";

    /// <summary>A favorite item.</summary>
    public const string Favorite = "ui.favorite";

    /// <summary>A folder.</summary>
    public const string AssetFolder = "asset.folder";

    /// <summary>An asset of no known kind.</summary>
    public const string AssetFile = "asset.file";

    /// <summary>A scene.</summary>
    public const string AssetScene = "asset.scene";

    /// <summary>A material.</summary>
    public const string AssetMaterial = "asset.material";

    /// <summary>A data asset.</summary>
    public const string AssetData = "asset.data";

    /// <summary>An image.</summary>
    public const string AssetImage = "asset.image";

    /// <summary>A 3D model or prefab.</summary>
    public const string AssetModel = "asset.model";

    /// <summary>A source script.</summary>
    public const string AssetScript = "asset.script";

    /// <summary>A text file.</summary>
    public const string AssetText = "asset.text";

    /// <summary>A UI document.</summary>
    public const string AssetUiDocument = "asset.ui-document";

    /// <summary>A UI style sheet.</summary>
    public const string AssetStyleSheet = "asset.style-sheet";

    /// <summary>An instance of a prefab.</summary>
    public const string PrefabInstance = "prefab.instance";

    /// <summary>An instance nested inside another prefab.</summary>
    public const string PrefabNested = "prefab.nested";

    /// <summary>A prefab variant.</summary>
    public const string PrefabVariant = "prefab.variant";

    /// <summary>An instance whose prefab is missing.</summary>
    public const string PrefabMissing = "prefab.missing";

    /// <summary>Every id above, which each built-in icon theme defines.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SceneSelect, SceneMove, SceneRotate, SceneScale, SceneTransform, SceneFrame, SceneSnap, SceneProjection,
        SceneNode, PlayStart, PlayPause, PlayStop, PlayRestart, PlayStep, CaretRight, CaretDown, Close, External,
        Revert, Lock, Unlock, Favorite, AssetFolder, AssetFile, AssetScene, AssetMaterial, AssetData, AssetImage,
        AssetModel, AssetScript, AssetText, AssetUiDocument, AssetStyleSheet, PrefabInstance, PrefabNested,
        PrefabVariant, PrefabMissing,
    ];
}
