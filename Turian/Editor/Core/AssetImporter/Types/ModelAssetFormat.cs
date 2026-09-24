namespace Turian.Editor.Core;

/// <summary>Model file formats.</summary>
public enum ModelAssetFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>Wavefront OBJ.</summary>
    Obj,

    /// <summary>Autodesk FBX.</summary>
    Fbx,

    /// <summary>GL Transmission Format (text).</summary>
    Gltf,

    /// <summary>GL Transmission Format (binary).</summary>
    Glb,

    /// <summary>COLLADA digital asset exchange.</summary>
    Dae,

    /// <summary>Blender native format.</summary>
    Blend
}
