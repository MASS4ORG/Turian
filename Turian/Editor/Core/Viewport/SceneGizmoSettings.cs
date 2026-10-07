namespace Turian.Editor.Core;

/// <summary>Controls Scene view gizmo visibility and the shared RGB colors of world axes.</summary>
[Gaya.EditorSetting("Scene Viewer/Gizmos", Id = "gaya.turian.sceneGizmos")]
public sealed class SceneGizmoSettings
{
    /// <summary>Gets or sets whether component and transform gizmos are visible.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Gets or sets whether the camera orientation control is visible.</summary>
    public bool ShowOrientation { get; set; } = true;

    /// <summary>Gets or sets the normalized RGB channels of the X axis color.</summary>
    [Gaya.EditorSetting("X Axis Color")]
    public Vector3 XColor { get; set; } = new(0.96f, 0.36f, 0.4f);

    /// <summary>Gets or sets the normalized RGB channels of the Y axis color.</summary>
    [Gaya.EditorSetting("Y Axis Color")]
    public Vector3 YColor { get; set; } = new(0.35f, 0.89f, 0.55f);

    /// <summary>Gets or sets the normalized RGB channels of the Z axis color.</summary>
    [Gaya.EditorSetting("Z Axis Color")]
    public Vector3 ZColor { get; set; } = new(0.28f, 0.55f, 1f);

    /// <summary>Returns the clamped axis color with the requested opacity.</summary>
    public Vector4 AxisColor(Vector3 axis, float opacity = 1f) =>
        new(Vector3.Clamp(XColor * MathF.Abs(axis.X) + YColor * MathF.Abs(axis.Y)
            + ZColor * MathF.Abs(axis.Z), Vector3.Zero, Vector3.One), Math.Clamp(opacity, 0f, 1f));
}
