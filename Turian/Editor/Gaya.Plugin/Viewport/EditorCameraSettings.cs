namespace Gaya.Plugin.Turian;

/// <summary>
/// How the Scene view's free camera responds to input. Applied to the viewport's
/// <c>SceneCameraController</c> every frame, so an edit is visible on the next drag.
/// </summary>
[EditorSetting("Scene Viewer/Camera")]
public sealed class EditorCameraSettings
{
    /// <summary>The shared grid preferences registered as a separate Scene Viewer settings page.</summary>
    internal SceneGridSettings Grid { get; } = new();

    /// <summary>The shared appearance preferences for Scene view gizmos.</summary>
    internal SceneGizmoSettings Gizmos { get; } = new();

    /// <summary>The shared transform snapping preferences.</summary>
    internal SceneToolSettings Tools { get; } = new();

    /// <summary>The effective camera shortcuts, resolved when the viewport requests these preferences.</summary>
    internal SceneNavigationBindings? Navigation { get; set; }

    /// <summary>The settings store notified by edits made in the Scene toolbar.</summary>
    internal IEditorSettings? Store { get; set; }

    /// <summary>Persists a Scene view preferences page changed outside the Settings panel.</summary>
    internal void NotifyChanged(string pageId) => Store?.NotifyChanged(pageId);

    /// <summary>Metres per second the camera flies at, before the Shift multiplier.</summary>
    [EditorSetting("Move Speed", Description = "Metres per second with the navigation shortcuts. Shift is four times this.")]
    [Range(0.1f, 100f)]
    public float MoveSpeed { get; set; } = 5f;

    /// <summary>Radians of rotation per pixel of pointer travel.</summary>
    [EditorSetting("Look Sensitivity", Description = "Radians the view turns per pixel of pointer travel.")]
    [Range(0.0005f, 0.05f)]
    public float LookSensitivity { get; set; } = 0.005f;

    /// <summary>Fraction of the move speed one scroll notch dollies by.</summary>
    [EditorSetting("Zoom Fraction", Description = "Fraction of the move speed one scroll notch travels.")]
    [Range(0.01f, 1f)]
    public float ZoomFraction { get; set; } = 0.1f;

    /// <summary>Gets or sets the Scene camera's vertical field of view in degrees.</summary>
    [Range(1f, 120f)]
    public float FieldOfView { get; set; } = 60f;

    /// <summary>Gets or sets the distance to the near clipping plane.</summary>
    [Range(0.001f, 100f)]
    public float NearClip { get; set; } = 0.01f;

    /// <summary>Gets or sets the distance to the far clipping plane.</summary>
    [Range(0.01f, 100000f)]
    public float FarClip { get; set; } = 500f;
}
