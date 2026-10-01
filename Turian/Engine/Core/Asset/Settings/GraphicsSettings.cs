namespace Turian.Engine.Core;

/// <summary>Project-wide rendering and texture defaults.</summary>
[CreateAssetMenu(fileName: "GraphicsSettings", path: "Settings/Graphics Settings")]
[TypeId("4af10a42-b4f9-5694-aa3b-f96a8f7f23aa")]
public class GraphicsSettings : ProjectSettingsAsset
{
    /// <summary>
    /// Default longest edge textures are imported at, or <c>0</c> for the source resolution. Stamped
    /// into each texture's meta when it is first imported, and overridable per texture. Applied by
    /// dropping leading mip levels, so it shrinks the cache as well as VRAM.
    /// </summary>
    public int TextureMaxResolution { get; set; }
}
