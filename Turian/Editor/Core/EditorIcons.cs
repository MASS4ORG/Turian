namespace Turian.Editor.Core;

/// <summary>
/// The icon ids of the asset kinds the editor core names, resolved by the studio through the active icon theme. They
/// are the same strings as the asset ids of <c>Gaya.Sdk.Icons</c>, which the core cannot reference.
/// </summary>
public static class EditorIcons
{
    /// <summary>An asset of no known kind.</summary>
    public const string File = "asset.file";

    /// <summary>A scene.</summary>
    public const string Scene = "asset.scene";

    /// <summary>A material.</summary>
    public const string Material = "asset.material";

    /// <summary>A data asset.</summary>
    public const string Data = "asset.data";

    /// <summary>An image.</summary>
    public const string Image = "asset.image";

    /// <summary>A 3D model or prefab.</summary>
    public const string Model = "asset.model";

    /// <summary>Source code.</summary>
    public const string Script = "asset.script";

    /// <summary>A text file.</summary>
    public const string Text = "asset.text";

    /// <summary>A UI document.</summary>
    public const string UiDocument = "asset.ui-document";

    /// <summary>A UI style sheet.</summary>
    public const string StyleSheet = "asset.style-sheet";
}
