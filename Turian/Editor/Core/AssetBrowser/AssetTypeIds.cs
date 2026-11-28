namespace Turian.Editor.Core;

/// <summary>The ids of the asset kinds <see cref="AssetTypeCatalog"/> ships with.</summary>
public static class AssetTypeIds
{
    /// <summary>Scenes and prefabs, whose tabs have the same id as their asset kind.</summary>
    public const string Scene = WorkspaceDocumentKinds.Scene;

    /// <summary>Materials.</summary>
    public const string Material = "turian.material";

    /// <summary>Data assets.</summary>
    public const string DataAsset = "turian.dataAsset";

    /// <summary>Images the engine bakes into textures.</summary>
    public const string Texture = "turian.texture";

    /// <summary>Source model files.</summary>
    public const string Model = "turian.model";

    /// <summary>C# source files.</summary>
    public const string Script = "turian.script";

    /// <summary>Text files the engine does not convert.</summary>
    public const string Text = "turian.text";

    /// <summary>UI documents.</summary>
    public const string UiDocument = "turian.uiDocument";

    /// <summary>UI style sheets.</summary>
    public const string UiStyleSheet = "turian.uiStyleSheet";
}
