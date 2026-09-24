namespace Turian.Engine.Core;

/// <summary>
/// Turian's values for the OAP <c>asset_type</c> category byte. The format
/// itself treats the byte as opaque; these values let tooling group and color a
/// package listing without opening each blob.
/// </summary>
public enum OapAssetType : byte
{
    /// <summary>Category is unknown or not set.</summary>
    Unknown = 0,

    /// <summary>A texture artifact (<c>.amtex</c> or a raw image).</summary>
    Texture = 1,

    /// <summary>A mesh artifact (<c>.ammesh</c>).</summary>
    Mesh = 2,

    /// <summary>A material definition.</summary>
    Material = 3,

    /// <summary>A prefab.</summary>
    Prefab = 4,

    /// <summary>A scene.</summary>
    Scene = 5,

    /// <summary>An audio clip.</summary>
    Audio = 6,

    /// <summary>Any other asset kind.</summary>
    Other = 7
}
