namespace Turian.Editor.Core;

/// <summary>Which handle is under the cursor or being dragged.</summary>
public enum TransformGizmoAxis
{
    /// <summary>No handle selected.</summary>
    None,

    /// <summary>Single axis X (red).</summary>
    X,

    /// <summary>Single axis Y (green).</summary>
    Y,

    /// <summary>Single axis Z (blue).</summary>
    Z,

    /// <summary>Planar XY (blue square).</summary>
    Xy,

    /// <summary>Planar XZ (green square).</summary>
    Xz,

    /// <summary>Planar YZ (red square).</summary>
    Yz,

    /// <summary>Uniform scale (center handle).</summary>
    Center,
}
