namespace Turian.Engine.Core;

/// <summary>Project-wide rendering and texture defaults.</summary>
[CreateAssetMenu(fileName: "GraphicsSettings", path: "Settings/Graphics Settings")]
[TypeId("a3000005-0000-4000-8000-000000000003")]
public class GraphicsSettings : ProjectSettingsAsset
{
    /// <summary>
    /// Default longest edge textures are imported at, or <c>0</c> for the source resolution. Stamped
    /// into each texture's meta when it is first imported, and overridable per texture. Applied by
    /// dropping leading mip levels, so it shrinks the cache as well as VRAM.
    /// </summary>
    public int TextureMaxResolution { get; set; }
}
