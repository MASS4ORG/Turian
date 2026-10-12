namespace Turian.Engine.Core;

/// <summary>Project-wide rendering and texture defaults.</summary>
[CreateAssetMenu(fileName: "GraphicsSettings", path: "Settings/Graphics Settings")]
[TypeId("4af10a42-b4f9-5694-aa3b-f96a8f7f23aa")]
public class GraphicsSettings : ProjectSettingsAsset
{
    /// <summary>MSAA sample count the renderer asks the device for; <see cref="DefaultMsaaSamples"/> by default.</summary>
    public const int DefaultMsaaSamples = 4;

    /// <summary>
    /// Requested MSAA sample count; the device caps it at the highest count both its color and depth
    /// attachments support. Defaults to 4× — a good trade-off on desktop and tile-based mobile GPUs.
    /// </summary>
    public int MsaaSamples { get; set; } = DefaultMsaaSamples;

    /// <summary>
    /// Default longest edge textures are imported at, or <c>0</c> for the source resolution. Stamped
    /// into each texture's meta when it is first imported, and overridable per texture. Applied by
    /// dropping leading mip levels, so it shrinks the cache as well as VRAM.
    /// </summary>
    public int TextureMaxResolution { get; set; }
}
