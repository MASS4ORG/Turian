namespace Turian.Editor.Core;

/// <summary>Stores independent transform snapping intervals without losing them when snapping is disabled.</summary>
[Gaya.EditorSetting("Scene Viewer/Transform", Id = "gaya.turian.sceneTransform")]
public sealed class SceneToolSettings
{
    /// <summary>Whether a group gizmo pivots around the selection centre rather than the active object.</summary>
    public bool CenterPivot { get; set; }

    /// <summary>Gets or sets whether transform gestures snap to the configured intervals.</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>Gets or sets the translation interval in world units.</summary>
    [Range(0f, 100f)]
    public float TranslationSnap { get; set; } = 1f;

    /// <summary>Gets or sets the rotation interval in degrees.</summary>
    [Range(0f, 180f)]
    public float RotationSnap { get; set; } = 15f;

    /// <summary>Gets or sets the scale interval.</summary>
    [Range(0f, 10f)]
    public float ScaleSnap { get; set; } = 0.1f;
}
